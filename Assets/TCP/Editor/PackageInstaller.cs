using UnityEditor;
using UnityEditor.PackageManager;

namespace work.ctrl3d.TCP
{
    [InitializeOnLoad]
    public class PackageInstaller
    {
        private const string UnityExtensionsName = "work.ctrl3d.unity-extensions";
        private const string UnityExtensionsGitUrl = "https://github.com/ctrl3d/UnityExtensions.git?path=Assets/UnityExtensions";
    
        private const string JsonConfigName = "work.ctrl3d.json-config";
        private const string JsonConfigGitUrl = "https://github.com/ctrl3d/JsonConfig.git?path=Assets/JsonConfig";
    
        static PackageInstaller()
        {
            // Unity API를 사용하여 안전하게 패키지 설치 (비동기 요청)
            // 이미 설치되어 있다면 Unity가 알아서 무시하거나 업데이트합니다.
            Client.Add(UnityExtensionsGitUrl);
            Client.Add(JsonConfigGitUrl);
        }
        
        // 기존의 위험한 파일 조작 코드(AddGitPackage, CheckPackageInstalled 등)는 제거합니다.
    }
}