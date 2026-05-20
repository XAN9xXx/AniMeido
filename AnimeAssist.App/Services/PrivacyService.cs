namespace AniMeido.App.Services
{
    public class PrivacyService
    {
        private const string RegistryKey = @"HKEY_CURRENT_USER\Software\AniMeido";

        public bool IsAccepted()
        {
            var val = Microsoft.Win32.Registry.GetValue(RegistryKey, "PrivacyAccepted", null);
            return val is int i && i == 1;
        }

        public void Accept()
        {
            Microsoft.Win32.Registry.SetValue(RegistryKey, "PrivacyAccepted", 1);
        }
    }
}
