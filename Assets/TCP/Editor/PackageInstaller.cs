using UnityEditor;
using UnityEditor.PackageManager;

namespace work.ctrl3d.TCP
{
    [InitializeOnLoad]
    public class PackageInstaller
    {
        private const string UnityExtensionsGitUrl = "https://github.com/ctrl3d/UnityExtensions.git?path=Assets/UnityExtensions";
        private const string JsonConfigGitUrl = "https://github.com/ctrl3d/JsonConfig.git?path=Assets/JsonConfig";
    
        static PackageInstaller()
        {
            Client.Add(UnityExtensionsGitUrl);
            Client.Add(JsonConfigGitUrl);
        }
    }
}