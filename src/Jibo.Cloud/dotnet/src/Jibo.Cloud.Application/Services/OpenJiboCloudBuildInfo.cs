using System.Globalization;

namespace Jibo.Cloud.Application.Services;

public static class OpenJiboCloudBuildInfo
{
    public const string Version = "1.0.20";
    public static readonly DateOnly PersonaBirthday = new(2026, 3, 22);

    public static string VersionWords => Version.Replace(".", " dot ");
    public static string PersonaBirthdayWords => PersonaBirthday.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture);

    public static string SpokenVersion => $"BEefy cloud version {VersionWords}.";

    public static string EsmlVersion =>
        $"BEefy cloud version<break time='10ms'/> {VersionWords.Replace(" ", "<break time='10ms' />")}.";
}