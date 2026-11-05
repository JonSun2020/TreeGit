using System;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public partial class SSHKeyGenerator : ObservableValidator
    {
        public SSHKeyGenerator() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh")) { }

        internal SSHKeyGenerator(string keyDirectory) => _keyDirectory = Path.GetFullPath(keyDirectory);

        public Models.SSHKeyType Type
        {
            get => _type;
            set => SetProperty(ref _type, value);
        }

        [CustomValidation(typeof(SSHKeyGenerator), nameof(ValidateName))]
        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value, true);
        }

        [CustomValidation(typeof(SSHKeyGenerator), nameof(ValidateEmail))]
        public string Email
        {
            get => _email;
            set => SetProperty(ref _email, value, true);
        }

        public bool UsePassphrase
        {
            get => _usePassphrase;
            set
            {
                if (SetProperty(ref _usePassphrase, value))
                {
                    ValidateProperty(_passphrase, nameof(Passphrase));
                    ValidateProperty(_confirmedPassphrase, nameof(ConfirmedPassphrase));
                }
            }
        }

        [CustomValidation(typeof(SSHKeyGenerator), nameof(ValidatePassphrase))]
        public string Passphrase
        {
            get => _passphrase;
            set
            {
                if (SetProperty(ref _passphrase, value, true))
                    ValidateProperty(_confirmedPassphrase, nameof(ConfirmedPassphrase));
            }
        }

        [CustomValidation(typeof(SSHKeyGenerator), nameof(ValidateConfirmedPassphrase))]
        public string ConfirmedPassphrase
        {
            get => _confirmedPassphrase;
            set => SetProperty(ref _confirmedPassphrase, value, true);
        }

        public string ErrorMessage
        {
            get => _errorMessage;
            set => SetProperty(ref _errorMessage, value);
        }

        public static ValidationResult ValidatePassphrase(string password, ValidationContext context)
        {
            var instance = (SSHKeyGenerator)context.ObjectInstance;
            if (!instance.UsePassphrase)
                return ValidationResult.Success;

            if (string.IsNullOrEmpty(password))
                return new ValidationResult(Message("PassphraseRequired", "Enter a passphrase."));

            if (!REG_PSWD_FORMAT().IsMatch(password))
                return new ValidationResult(Message("PassphraseFormat", "Use letters, numbers, or _ + - = @ # $ % ! &."));

            return ValidationResult.Success;
        }

        public static ValidationResult ValidateConfirmedPassphrase(string confirmedPassword, ValidationContext context)
        {
            var instance = (SSHKeyGenerator)context.ObjectInstance;
            if (!instance.UsePassphrase)
                return ValidationResult.Success;

            if (confirmedPassword != instance.Passphrase)
                return new ValidationResult(Message("PassphraseMismatch", "The passphrases do not match."));

            return ValidationResult.Success;
        }

        public static ValidationResult ValidateName(string name, ValidationContext _)
        {
            if (string.IsNullOrEmpty(name)) return new ValidationResult(Message("NameRequired", "Enter a key name."));
            return REG_NAME_FORMAT().IsMatch(name) ? ValidationResult.Success :
                new ValidationResult(Message("NameFormat", "Use letters, numbers, underscores, or hyphens."));
        }

        public static ValidationResult ValidateEmail(string email, ValidationContext _)
        {
            if (string.IsNullOrEmpty(email)) return new ValidationResult(Message("EmailRequired", "Enter an email address."));
            return new EmailAddressAttribute().IsValid(email) ? ValidationResult.Success :
                new ValidationResult(Message("EmailFormat", "Enter a valid email address."));
        }

        public Models.SSHKeyPair Run()
        {
            ErrorMessage = string.Empty;

            ValidateAllProperties();
            if (HasErrors)
                return null;

            var dir = _keyDirectory;
            if (!Directory.Exists(dir))
            {
                try
                {
                    Directory.CreateDirectory(dir);
                }
                catch (Exception)
                {
                    ErrorMessage = GenerationFailed();
                    return null;
                }
            }

            var type = _type?.Cmdline ?? "-t ed25519";
            var passphrase = _usePassphrase ? _passphrase : string.Empty;
            var keyFile = Path.Combine(dir, _name);
            if (File.Exists(keyFile) || File.Exists(keyFile + ".pub") || Directory.Exists(keyFile) || Directory.Exists(keyFile + ".pub"))
            {
                ErrorMessage = Message("Exists", "This key name is already in use. Choose another name.");
                return null;
            }
            var start = new ProcessStartInfo();
            start.FileName = "ssh-keygen";
            start.Arguments = $"-q {type} -N {passphrase.Quoted()} -C {_email.Quoted()} -f {keyFile.Quoted()}";
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.RedirectStandardInput = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            if (OperatingSystem.IsWindows())
            {
                var ssh = Native.GitExecutableLocator.ConfigureProcess(start, Native.OS.GitExecutable);
                if (ssh != null)
                {
                    var generator = Path.Combine(Path.GetDirectoryName(ssh), "ssh-keygen.exe");
                    if (File.Exists(generator)) start.FileName = generator;
                }
            }

            try
            {
                using var proc = Native.ProcessLauncher.Start(start);
                proc.StandardInput.Close();
                var output = proc.StandardOutput.ReadToEndAsync();
                var error = proc.StandardError.ReadToEndAsync();
                proc.WaitForExit();
                System.Threading.Tasks.Task.WhenAll(output, error).GetAwaiter().GetResult();
                if (proc.ExitCode != 0)
                {
                    ErrorMessage = GenerationFailed();
                    return null;
                }
            }
            catch (Exception)
            {
                ErrorMessage = GenerationFailed();
                return null;
            }

            var publicKeyFile = keyFile + ".pub";
            if (File.Exists(keyFile) && File.Exists(publicKeyFile))
                return new Models.SSHKeyPair(keyFile, publicKeyFile);

            ErrorMessage = GenerationFailed();
            return null;
        }

        private static string GenerationFailed() => Message("Failed", "The key could not be created. Check the key folder and SSH tools, then try again.");

        private static string Message(string key, string fallback) => App.Current == null ? fallback : App.Text("SSHKeyHelper.Generator." + key);

        [GeneratedRegex(@"^[a-zA-Z0-9_\-]+$")]
        private static partial Regex REG_NAME_FORMAT();

        [GeneratedRegex(@"^[0-9a-zA-Z_\-\@\#\$\%\!\&\+\=]+$")]
        private static partial Regex REG_PSWD_FORMAT();

        private Models.SSHKeyType _type = Models.SSHKeyType.Supported[0];
        private string _name;
        private string _email;
        private bool _usePassphrase;
        private string _passphrase;
        private string _confirmedPassphrase;
        private string _errorMessage;
        private readonly string _keyDirectory;
    }
}
