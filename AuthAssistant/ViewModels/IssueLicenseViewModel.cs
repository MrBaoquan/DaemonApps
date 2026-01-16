using ReactiveUI;
using System;

namespace AuthAssistant.ViewModels
{
    public class IssueLicenseViewModel : ViewModelBase
    {
        private string _username = string.Empty;
        public string Username
        {
            get => _username;
            set => this.RaiseAndSetIfChanged(ref _username, value);
        }

        private string _phoneNumber = string.Empty;
        public string PhoneNumber
        {
            get => _phoneNumber;
            set => this.RaiseAndSetIfChanged(ref _phoneNumber, value);
        }

        private DateTimeOffset _expiredAt = DateTimeOffset.Now.AddYears(1);
        public DateTimeOffset ExpiredAt
        {
            get => _expiredAt;
            set => this.RaiseAndSetIfChanged(ref _expiredAt, value);
        }

        private bool _isSuperAdmin = false;
        public bool IsSuperAdmin
        {
            get => _isSuperAdmin;
            set => this.RaiseAndSetIfChanged(ref _isSuperAdmin, value);
        }
    }
}
