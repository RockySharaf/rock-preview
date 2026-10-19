using System.Security;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace Anything;

/// <summary>
/// Windows notifications, under Rock Preview's AppUserModelID (Windows takes the name and icon from the Start menu
/// shortcut carrying it). Clicking one opens a web page through the shell (protocol activation), which works even
/// after Rock Preview has exited.
/// </summary>
internal static class Toast
{
    /// <summary>Shows a notification that opens <paramref name="url"/>; false when Windows refuses it.</summary>
    public static bool TryShow(string title, string text, string url, string button)
    {
        try
        {
            string link = SecurityElement.Escape(url);
            var xml = new XmlDocument();
            xml.LoadXml(
                $"<toast activationType=\"protocol\" launch=\"{link}\">" +
                "<visual><binding template=\"ToastGeneric\">" +
                $"<text>{SecurityElement.Escape(title)}</text><text>{SecurityElement.Escape(text)}</text>" +
                "</binding></visual>" +
                $"<actions><action content=\"{SecurityElement.Escape(button)}\" activationType=\"protocol\" arguments=\"{link}\"/></actions>" +
                "</toast>");
            ToastNotificationManager.CreateToastNotifier(Shortcuts.AppId).Show(new ToastNotification(xml));
            return true;
        }
        catch (Exception) { return false; }
    }
}
