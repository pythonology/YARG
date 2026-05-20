using TMPro;
using UnityEngine;
using YARG.Core.Extensions;
using YARG.Core.Game;
using YARG.Menu.ScoreScreen;
using YARG.Scores;

namespace YARG.Menu.MusicLibrary
{
    public class SongLeaderboardRow : MonoBehaviour
    {
        [SerializeField]
        private TMP_Text _profileName;
        [SerializeField]
        private TMP_Text _engineName;
        [SerializeField]
        private TMP_Text _songSpeed;
        [SerializeField]
        private InstrumentDifficultyView _instrumentDifficultyView;
        [SerializeField]
        private TMP_Text _score;
        [SerializeField]
        private RectTransform _modifierIconContainer;
        [SerializeField]
        private ModifierIcon _modifierIconPrefab;

        public void Bind(SongLeaderboardEntry entry, string displayName, string engineName)
        {
            _profileName.text = displayName;
            _engineName.text  = engineName;
            _songSpeed.text   = $"{entry.SongSpeed:0.00}x";
            _score.text       = entry.Score.ToString("N0");
            _instrumentDifficultyView.SetInfo(new ViewType.ScoreInfo
            {
                Percent    = entry.GetPercent(),
                Score      = entry.Score,
                Difficulty = entry.Difficulty,
                Instrument = entry.Instrument,
                IsFc       = entry.IsFc,
            });
            ShowModifiers(entry.Modifiers);
        }

        private void ShowModifiers(Modifier? mods)
        {
            for (int i = _modifierIconContainer.childCount - 1; i >= 0; i--)
            {
                Destroy(_modifierIconContainer.GetChild(i).gameObject);
            }

            if (!mods.HasValue || mods.Value == Modifier.None)
            {
                return;
            }

            foreach (var flag in EnumExtensions<Modifier>.Values)
            {
                if (flag == Modifier.None) continue;
                if (!mods.Value.HasFlag(flag)) continue;

                var icon = Instantiate(_modifierIconPrefab, _modifierIconContainer);
                icon.InitializeForModifier(flag);
            }
        }
    }
}
