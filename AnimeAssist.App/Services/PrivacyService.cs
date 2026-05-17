using Windows.Storage;

namespace AnimeAssist.App.Services
{
    public class PrivacyService
    {
        public bool IsAccepted()
        {
            return ApplicationData.Current.LocalSettings.Values["PrivacyAccepted"] is true;
        }

        public void Accept()
        {
            ApplicationData.Current.LocalSettings.Values["PrivacyAccepted"] = true;
        }
    }
}
