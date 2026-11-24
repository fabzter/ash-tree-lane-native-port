using UnityShim.Backend;

namespace UnityEngine.SceneManagement
{
    public struct Scene
    {
        public int buildIndex { get; set; }
        public string name { get; set; }
    }

    public static class SceneManager
    {
        public static Scene GetActiveScene() => new Scene
        {
            buildIndex = EngineHost.SceneController?.ActiveSceneIndex ?? 0,
            name = EngineHost.SceneController?.ActiveSceneName ?? "level0"
        };

        public static void LoadScene(int sceneBuildIndex)
        {
            EngineHost.SceneController?.RequestLoadScene(sceneBuildIndex);
        }
    }
}
