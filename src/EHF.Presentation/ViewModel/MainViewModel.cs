using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using EccHsmEncryptor.Presentation.DesignData;
using EccHsmEncryptor.Presentation.Views;
using EncryptionSuite.Contract;
using EncryptionSuite.Encryption;
using EncryptionSuite.Encryption.Hybrid;
using GalaSoft.MvvmLight;
using GalaSoft.MvvmLight.CommandWpf;
using GalaSoft.MvvmLight.Ioc;
using GalaSoft.MvvmLight.Messaging;
using GalaSoft.MvvmLight.Threading;
using Microsoft.Win32;
using Storage;
using EllipticCurveCryptographer = EncryptionSuite.Encryption.NitroKey.EllipticCurveCryptographer;

namespace EccHsmEncryptor.Presentation.ViewModel
{
    public class MainViewModel : ViewModelBase
    {
        #region .ctor

        public MainViewModel()
        {
            if (this.IsInDesignMode || IsInDesignModeStatic)
            {
                #region Design Data

                this.FilePath = @"C:\temp\document.docx";
                this.Progress = 80;
                this.PublicKeys = new ObservableCollection<EcKeyPairInfoViewModel>()
                {
                    new EcKeyPairInfoViewModel()
                    {
                        KeyPairInfos = DesignDataFactory.CreateDesignData<EcKeyPairInfo>("My Key #1", "Token White", "DENK0100123"),
                        IsSelected = true,
                        Description = "My buisness token"
                    },
                    new EcKeyPairInfoViewModel()
                    {
                        KeyPairInfos = DesignDataFactory.CreateDesignData<EcKeyPairInfo>("My Key #2", "Token White", "DENK0100123"),
                        IsSelected = false,
                        Description = "My private token"
                    },
                    new EcKeyPairInfoViewModel()
                    {
                        KeyPairInfos = DesignDataFactory.CreateDesignData<EcKeyPairInfo>("My Key #3", "Token Black", "DENK0100321"),
                        IsSelected = false,
                        Description = "My private backup token"
                    },
                };
                this.AvailableHardwareTokens = new List<EcKeyPairInfo>()
                {
                    DesignDataFactory.CreateDesignData<EcKeyPairInfo>("My Key #1", "Token White", "DENK0100123"),
                    DesignDataFactory.CreateDesignData<EcKeyPairInfo>("My Key #3", "Token Black", "DENK0100321"),
                };
                this.SelectedAvailableHardwareToken = this.AvailableHardwareTokens.First();

                #endregion
            }
            else
            {
                this.LoadedEvent = new RelayCommand(this.LoadedEventHandling);

                this.PublicKeySettingsCommand = new RelayCommand(this.PublicKeySettingsCommandHandling);
                this.EncryptCommand = new RelayCommand(this.EncryptCommandHandling, this.EncryptCommandCanExecute);
                this.DecryptCommand = new RelayCommand(this.DecryptCommandHandling, this.DecryptCommandCanExecute);
                this.CancelCommand = new RelayCommand(this.CancelCommandHandling, this.CancelCommandCanExecute);
                this.RefreshHsmListCommand = new RelayCommand(this.RefreshHsmListCommandHandling);
                this.HelpCommand = new RelayCommand(this.HelpCommandHandling);
                this.SettingsCommand = new RelayCommand(this.SettingsCommandHandling);
                this.LoadFileCommand = new RelayCommand(this.LoadFileCommandHandling);
                this.AvailableHardwareTokensIsBusy = true;
                this.PublicKeysIsBusy = true;

                base.MessengerInstance.Register<Messages.StorageChange>(this, change =>
                {
                    switch (change.StorageName)
                    {
                        case StorageNames.PublicKeys:
                            this.RefreshPublicKeys();
                            break;
                        case StorageNames.State:
                            this.SaveState();
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                    change.CompletedCallback();
                });               
            }
        }

        #endregion

        #region Commands

        public RelayCommand DecryptCommand { get; set; }
        public RelayCommand EncryptCommand { get; set; }
        public RelayCommand PublicKeySettingsCommand { get; set; }
        public RelayCommand LoadFileCommand { get; set; }
        public RelayCommand LoadedEvent { get; set; }
        public RelayCommand HelpCommand { get; set; }
        public RelayCommand CancelCommand { get; set; }
        public RelayCommand RefreshHsmListCommand { get; set; }
        public RelayCommand SettingsCommand { get; set; }

        #endregion

        #region Command Handling

        private void PublicKeySettingsCommandHandling()
        {
            WindowInvoker.ShowWindow(WindowInvoker.Windows.PublicKeySettings);
        }

        private bool EncryptCommandCanExecute()
        {
            if (this.IsBusy) return false;

            if (!File.Exists(this.FilePath))
                return false;

            if (!this.PublicKeys.Any(model => model.IsSelected))
                return false;

            return true;
        }

        private async void EncryptCommandHandling()
        {
            this.IsBusy = true;

            await Task.Run(() =>
            {
                var targetPath = this.HideFilename
                    ? Path.Combine(Path.GetDirectoryName(this.FilePath), $"{Guid.NewGuid()}.enc")
                    : $"{this.FilePath}.enc";

                using (var input = File.OpenRead(this.FilePath))
                using (var output = File.Create(targetPath))
                {
                    var selectedPublicKeys = this.PublicKeys.Where(model => model.IsSelected).Select(model => model.KeyPairInfos.PublicKey.ExportPublicKey());

                    HybridEncryption.Encrypt(input, output, new HybridEncryption.EncryptionParameter
                    {
                        Progress = this.ReportProgress,
                        IsCanceled = () => this.IsCanceled,
                        PublicKeys = selectedPublicKeys,
                        Filename = Path.GetFileName(this.FilePath),
                    });
                }
            });

            this.IsBusy = false;
        }

        private void ReportProgress(double d)
        {
            DispatcherHelper.CheckBeginInvokeOnUI(() => { this.Progress = d; });
        }

        private bool DecryptCommandCanExecute()
        {
            if (this.IsBusy) return false;

            if (!File.Exists(this.FilePath))
                return false;

            if (this.SelectedAvailableHardwareToken == null)
                return false;

            var hasFileSignature = EncryptionSuite.Encryption.FileOperation.HasFileSignature(this.FilePath);
            if (!hasFileSignature)
                return false;

            return true;
        }

        private async void DecryptCommandHandling()
        {
            var result = new PasswordWindow().ShowDialog();

            if (result == null || !(bool) result)
                return;

            this.IsBusy = true;

            var hsmPin = SimpleIoc.Default.GetInstance<PasswordViewModel>().Password;

            await Task.Run(() =>
            {
                DecryptInfo info;
                var targetPath = $"{this.FilePath}.dec";

                using (var input = File.OpenRead(this.FilePath))
                using (var output = File.Create(targetPath))
                {
                    info = HybridEncryption.Decrypt(input, output, new HybridEncryption.DecryptionParameter()
                    {
                        Progress = this.ReportProgress,
                        IsCanceled = () => this.IsCanceled,
                        Password = hsmPin,
                    });
                }

                var decryptedFileName = Path.Combine(Path.GetDirectoryName(this.FilePath), info.FileName);
                if (File.Exists(decryptedFileName))
                {
                    var dto = new DateTimeOffset(DateTime.Now);
                    decryptedFileName += $".{dto.ToUnixTimeMilliseconds()}{Path.GetExtension(info.FileName)}";
                }

                File.Move(targetPath, decryptedFileName);
            });

            this.IsBusy = false;
        }

        private async void LoadedEventHandling()
        {
            var tasks = new List<Task>
            {
                Task.Run(() => this.RefreshPublicKeys()),
                Task.Run(() => this.RefreshAvailableHardwareToken())
            };

            await Task.WhenAll(tasks);

            var tasksSecondPass = new List<Task>
            {
                Task.Run(() => this.LoadState()),
            };

            await Task.WhenAll(tasksSecondPass);
        }

        private void SaveState()
        {
            var state = new DataViewRowState
            {
                SelectedPublicKeys = this.PublicKeys.Where(model => model.IsSelected).Select(model => model.KeyPairInfos.PublicKey.ToAns1()).ToList()
            };
            new LocalStorageManager().RemoveAll("SelectedPublicKeysAns1");
            new LocalStorageManager().Add(state, "SelectedPublicKeysAns1");
        }

        private void LoadState()
        {
            var state = new LocalStorageManager().GetAll<DataViewRowState>("SelectedPublicKeysAns1")?.FirstOrDefault();
            if (state == null)
                return;

            var toSelect = this.PublicKeys.Where(model => state.SelectedPublicKeys.Any(bytes => bytes.SequenceEqual(model.KeyPairInfos.PublicKey.ToAns1()))).ToList();
            toSelect.ForEach(model => model.IsSelected = true);

            CommandManager.InvalidateRequerySuggested();
        }

        public class DataViewRowState
        {
            public List<byte[]> SelectedPublicKeys { get; set; }
        }

        private bool CancelCommandCanExecute()
        {
            return this.isBusy;
        }

        private void CancelCommandHandling()
        {
            this.IsCanceled = true;
        }

        #endregion

        private void RefreshPublicKeys()
        {
            List<EcKeyPairInfoViewModel> loadedKeys;
            try
            {
                loadedKeys = new LocalStorageManager().GetAll<EcKeyPairInfoViewModel>(StorageNames.PublicKeys.ToString()).ToList();
            }
            catch (Exception e)
            {
                loadedKeys = Enumerable.Empty<EcKeyPairInfoViewModel>().ToList();
            }

            DispatcherHelper.CheckBeginInvokeOnUI(() =>
            {
                this.PublicKeys = new ObservableCollection<EcKeyPairInfoViewModel>(loadedKeys);

                if (!loadedKeys.Any())
                    this.PublicKeysNotAvailable = true;

                this.PublicKeysIsBusy = false;
            });
        }

        private void RefreshAvailableHardwareToken()
        {
            EcKeyPairInfo[] nitroKeys;
            try
            {
                nitroKeys = EllipticCurveCryptographer.GetEcKeyPairInfos();
            }
            catch (Exception e)
            {
                nitroKeys = Enumerable.Empty<EcKeyPairInfo>().ToArray();
            }


            DispatcherHelper.CheckBeginInvokeOnUI(() =>
            {
                this.AvailableHardwareTokens = new List<EcKeyPairInfo>(nitroKeys);
                this.SelectedAvailableHardwareToken = this.AvailableHardwareTokens.FirstOrDefault();
                this.AvailableHardwareTokensIsBusy = false;
            });
        }

        private void RefreshHsmListCommandHandling()
        {
            this.RefreshAvailableHardwareToken();
        }

        private void HelpCommandHandling()
        {
            Process.Start("https://github.com/dhcgn/ECC-HSM-Encryptor/wiki");
        }

        private void SettingsCommandHandling()
        {
            WindowInvoker.ShowWindow(WindowInvoker.Windows.Settings);
        }

        private void LoadFileCommandHandling()
        {
            var dialog = new OpenFileDialog
            {
                Multiselect = true,
            };
            if (dialog.ShowDialog() == true)
            {
                this.SetFilenamesToView(dialog.FileNames);
            }
        }

        public void SetFilenamesToView(string[] files)
        {
            if (files == null || !files.Any())
                return;

            if (files.Length == 1 && !File.GetAttributes(files[0]).HasFlag(FileAttributes.Directory))
            {
                this.FilePath = files[0];
                return;
            }

            var result = MessageBox.Show("This is not a single file, files or folder need to be zipped, continue?", "Zip files or folder?", MessageBoxButton.YesNo);
            if (result != MessageBoxResult.Yes)
                return;

            var folder = Path.GetDirectoryName(files[0]);
            var tempFilePath = Path.Combine(folder, $"{Guid.NewGuid()}.zip");

            using (ZipArchive archive = ZipFile.Open(tempFilePath, ZipArchiveMode.Update))
            {
                foreach (var file in files)
                {
                    archive.CreateEntryFromFile(file, Path.GetFileName(file));
                }
            }

            this.FilePath = tempFilePath;
        }

        #region Properties

        private List<EcKeyPairInfo> availableHardwareTokens;

        public List<EcKeyPairInfo> AvailableHardwareTokens
        {
            get => this.availableHardwareTokens;
            set => this.Set(ref this.availableHardwareTokens, value);
        }

        private EcKeyPairInfo selectedAvailableHardwareToken;

        public EcKeyPairInfo SelectedAvailableHardwareToken
        {
            get => this.selectedAvailableHardwareToken;
            set => this.Set(ref this.selectedAvailableHardwareToken, value);
        }

        private ObservableCollection<EcKeyPairInfoViewModel> publicKeys;

        public ObservableCollection<EcKeyPairInfoViewModel> PublicKeys
        {
            get => this.publicKeys;
            set => this.Set(ref this.publicKeys, value);
        }

        private string filePath;

        public string FilePath
        {
            get
            {
                CommandManager.InvalidateRequerySuggested();
                return this.filePath;
            }
            set => this.Set(ref this.filePath, value);
        }

        private bool showDropPanel;

        public bool ShowDropPanel
        {
            get => this.showDropPanel;
            set => this.Set(ref this.showDropPanel, value);
        }

        private bool availableHardwareTokensIsBusy;

        public bool AvailableHardwareTokensIsBusy
        {
            get => this.availableHardwareTokensIsBusy;
            set => this.Set(ref this.availableHardwareTokensIsBusy, value);
        }

        private bool publicKeysIsBusy;

        public bool PublicKeysIsBusy
        {
            get => this.publicKeysIsBusy;
            set => this.Set(ref this.publicKeysIsBusy, value);
        }

        private bool publicKeysNotAvailable;

        public bool PublicKeysNotAvailable
        {
            get => this.publicKeysNotAvailable;
            set => this.Set(ref this.publicKeysNotAvailable, value);
        }

        private double progress;

        public double Progress
        {
            get => this.progress;
            set => this.Set(ref this.progress, value);
        }

        private bool isCanceled;

        public bool IsCanceled
        {
            get => this.isCanceled;
            set => this.Set(ref this.isCanceled, value);
        }

        private bool isBusy;

        public bool IsBusy
        {
            get => this.isBusy;
            set => this.Set(ref this.isBusy, value);
        }

        private bool hideFilename = true;

        public bool HideFilename
        {
            get => this.hideFilename;
            set => this.Set(ref this.hideFilename, value);
        }

        #endregion
    }
}