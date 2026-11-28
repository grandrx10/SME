using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// The release this build belongs to: <c>Resources/GameVersion.txt</c>, written by <c>Tools/release.ps1</c>.
    /// Lobbies are named after it so players on different versions never meet.
    /// </summary>
    public static class GameVersion
    {
        static string _current;

        public static string Current
        {
            get
            {
                if (_current == null)
                {
                    var t = Resources.Load<TextAsset>("GameVersion");
                    _current = t != null && t.text.Trim().Length > 0 ? t.text.Trim() : "dev";
                }
                return _current;
            }
        }
    }
}
