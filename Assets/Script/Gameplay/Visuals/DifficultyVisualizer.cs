using System.Collections.Generic;
using UnityEngine;
using YARG.Core.Chart;
using YARG.Core.Song;
using YARG.Gameplay.Player;

namespace YARG.Gameplay.Visuals
{
    /// <summary>
    /// Practice-mode debug overlay drawn via OnGUI (so it isn't subject to
    /// URP track-camera shader filtering — that was hiding our previous
    /// 3D bars / TMP labels).
    ///
    /// <para>Two side bars at the screen edges show live fret and strum
    /// complexity for the current song time, with peak labels that hold
    /// briefly before decaying. A floating label is drawn above each
    /// upcoming note, projected through
    /// <see cref="HighwayCameraRendering.GetTrackPositionScreenSpace"/>,
    /// showing that note's fret/strum complexity values.</para>
    /// </summary>
    public class DifficultyVisualizer : MonoBehaviour
    {
        // -------- Bar layout (in screen pixels) --------
        private const float BAR_W                    = 50f;
        private const float BAR_H                    = 500f;
        // Distance from screen edge to the bar's outer edge.
        private const float BAR_SCREEN_INSET_X       = 220f;
        // Bar vertical center as a fraction of screen height (0.5 = middle).
        private const float BAR_SCREEN_CENTER_Y_FRAC = 0.55f;
        private const float LABEL_W                  = 140f;
        private const float LABEL_H                  = 24f;

        // How long a "recent peak" stays latched before decaying.
        private const float PEAK_HOLD_SECONDS = 4f;

        // Per-note label
        private const float NOTE_LABEL_W   = 60f;
        private const float NOTE_LABEL_H   = 30f;
        private const float NOTE_LABEL_Y_OFFSET_PX = 28f; // pixels above note on screen

        private static readonly Color FretColor  = new(1.00f, 0.30f, 0.30f, 1f);
        private static readonly Color StrumColor = new(0.30f, 1.00f, 0.45f, 1f);
        private static readonly Color BgColor    = new(0f, 0f, 0f, 0.55f);

        private TrackPlayer            _trackPlayer;
        private GameManager            _gameManager;
        private HighwayCameraRendering _highwayRenderer;
        private DifficultyAnalysis     _analysis;
        private List<GuitarNote>       _notes;
        private int                    _highwayIndex;

        // Per-channel song-wide max for bar normalization.
        private float _fretMax;
        private float _strumMax;

        // Recent-peak state.
        private float  _fretPeak,  _strumPeak;
        private double _fretPeakAt, _strumPeakAt;

        // Reusable 1×1 textures for OnGUI bar draws.
        private Texture2D _texFret;
        private Texture2D _texStrum;
        private Texture2D _texBg;
        private GUIStyle  _labelStyle;
        private GUIStyle  _smallLabelStyle;

        // Cursor for note iteration each frame (notes are time-sorted, so we
        // can advance forward without rescanning the whole list).
        private int _noteCursor;

        public void Initialize(TrackPlayer trackPlayer, GameManager gameManager, SongChart chart)
        {
            _trackPlayer  = trackPlayer;
            _gameManager  = gameManager;
            _highwayIndex = trackPlayer.HighwayIndex;

            // HighwayCameraRendering lives on the per-player Camera child. Use
            // includeInactive so we still find it if the Camera is disabled at
            // some point in init order. Search from the BaseVisual root so we
            // pick up the right player's renderer in multiplayer.
            _highwayRenderer = trackPlayer.GetComponentInChildren<HighwayCameraRendering>(includeInactive: true);
            if (_highwayRenderer == null)
            {
                YARG.Core.Logging.YargLogger.LogWarning(
                    "DifficultyVisualizer: HighwayCameraRendering not found — per-note labels and bar-anchored positioning will fall back.");
            }

            var profile = trackPlayer.Player.Profile;
            _analysis = StarRatingCalculator.ComputeAnalysis(
                chart, profile.CurrentInstrument, profile.CurrentDifficulty);

            if (_analysis == null || _analysis.Fret.IsEmpty || _analysis.Strum.IsEmpty)
            {
                enabled = false;
                return;
            }

            // Pull the chart notes for this player so we can label each one.
            // ComputeAnalysis already enforced this is a 5-fret instrument with
            // a non-empty difficulty track.
            var track = chart.GetFiveFretTrack(profile.CurrentInstrument);
            if (!track.TryGetDifficulty(profile.CurrentDifficulty, out var diffTrack))
            {
                enabled = false;
                return;
            }
            _notes = diffTrack.Notes;

            _fretMax  = ChannelMax(_analysis.Fret.Values);
            _strumMax = ChannelMax(_analysis.Strum.Values);
        }

        private static float ChannelMax(float[] values)
        {
            float m = 0f;
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i] > m) m = values[i];
            }
            return m > 0f ? m : 1f;
        }

        private void EnsureGuiResources()
        {
            if (_texFret != null) return;

            _texFret  = MakeTex(FretColor);
            _texStrum = MakeTex(StrumColor);
            _texBg    = MakeTex(BgColor);

            _labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize  = 18,
                alignment = TextAnchor.MiddleLeft,
                normal    = { textColor = Color.white },
            };
            _smallLabelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize  = 12,
                alignment = TextAnchor.MiddleCenter,
                normal    = { textColor = Color.white },
            };
        }

        private static Texture2D MakeTex(Color c)
        {
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, mipChain: false);
            tex.SetPixel(0, 0, c);
            tex.Apply();
            return tex;
        }

        private void Update()
        {
            // Track recent peaks every frame regardless of OnGUI events.
            if (_analysis == null) return;

            int idx = SampleIndex(_gameManager.VisualTime, _analysis.Fret.Values.Length);
            if (idx < 0) return;

            float fret  = _analysis.Fret.Values[idx];
            float strum = _analysis.Strum.Values[idx];
            double t    = _gameManager.VisualTime;

            UpdatePeak(fret,  ref _fretPeak,  ref _fretPeakAt,  t);
            UpdatePeak(strum, ref _strumPeak, ref _strumPeakAt, t);
        }

        private static void UpdatePeak(float current, ref float peak, ref double peakAt, double now)
        {
            if (current >= peak || (now - peakAt) > PEAK_HOLD_SECONDS)
            {
                peak   = current;
                peakAt = now;
            }
        }

        private void OnGUI()
        {
            if (_analysis == null) return;

            EnsureGuiResources();

            int idx = SampleIndex(_gameManager.VisualTime, _analysis.Fret.Values.Length);
            if (idx < 0) return;
            float fret  = _analysis.Fret.Values[idx];
            float strum = _analysis.Strum.Values[idx];

            float barCenterY = Screen.height * BAR_SCREEN_CENTER_Y_FRAC;

            DrawBar(side: -1, centerY: barCenterY,
                label: "FRET",  current: fret,  peak: _fretPeak,  max: _fretMax,  fill: _texFret);
            DrawBar(side: +1, centerY: barCenterY,
                label: "STRUM", current: strum, peak: _strumPeak, max: _strumMax, fill: _texStrum);

            DrawNoteLabels(_gameManager.VisualTime);
        }

        // side = -1 places the bar BAR_SCREEN_INSET_X pixels from the left edge,
        //        +1 places it BAR_SCREEN_INSET_X pixels from the right edge.
        private void DrawBar(int side, float centerY,
            string label, float current, float peak, float max, Texture2D fill)
        {
            float barX = side < 0
                ? BAR_SCREEN_INSET_X
                : Screen.width - BAR_SCREEN_INSET_X - BAR_W;
            float barBottomY = centerY + BAR_H * 0.5f;
            float barTopY    = barBottomY - BAR_H;

            // Background slot
            GUI.DrawTexture(new Rect(barX, barTopY, BAR_W, BAR_H), _texBg);

            // Filled portion (grows up from the bottom)
            float fillFrac = Mathf.Clamp01(current / max);
            float fillH    = BAR_H * fillFrac;
            GUI.DrawTexture(new Rect(barX, barBottomY - fillH, BAR_W, fillH), fill);

            // Peak tick line
            float peakFrac = Mathf.Clamp01(peak / max);
            float peakY    = barBottomY - BAR_H * peakFrac;
            GUI.DrawTexture(new Rect(barX - 3f, peakY - 1.5f, BAR_W + 6f, 3f), fill);

            // Labels stack outward from the bar (away from the track).
            float labelX = side < 0
                ? barX - LABEL_W - 6f
                : barX + BAR_W + 6f;
            var labelAlign = side < 0 ? TextAnchor.MiddleRight : TextAnchor.MiddleLeft;

            var styleHeader = new GUIStyle(_labelStyle) { alignment = labelAlign, fontStyle = FontStyle.Bold };
            var styleValue  = new GUIStyle(_labelStyle) { alignment = labelAlign };

            GUI.Label(new Rect(labelX, barTopY,            LABEL_W, LABEL_H), label,                styleHeader);
            GUI.Label(new Rect(labelX, barTopY + 22f,      LABEL_W, LABEL_H), $"now {current:F2}",  styleValue);
            GUI.Label(new Rect(labelX, barTopY + 44f,      LABEL_W, LABEL_H), $"peak {peak:F2}",    styleValue);
        }

        private void DrawNoteLabels(double t)
        {
            if (_highwayRenderer == null || _notes == null) return;

            double fadePos = _trackPlayer.ZeroFadePosition;
            float  ns      = _trackPlayer.NoteSpeed;
            double zRange  = fadePos - TrackPlayer.STRIKE_LINE_POS;

            // How far ahead in time to label (seconds visible at current scroll
            // speed plus a small margin).
            double visibleSeconds = zRange / ns + 0.25;

            // Advance the cursor past notes that are now behind the strikeline.
            while (_noteCursor < _notes.Count && _notes[_noteCursor].Time < t - 0.1)
            {
                _noteCursor++;
            }

            int sampleCount = _analysis.Fret.Values.Length;

            for (int i = _noteCursor; i < _notes.Count; i++)
            {
                var note    = _notes[i];
                double dt   = note.Time - t;
                if (dt > visibleSeconds) break;     // future, beyond visible
                if (dt < -0.1)            continue; // shouldn't happen due to cursor

                // Normalized track coords expected by GetTrackPositionScreenSpace:
                // x in [0,1] across width, y in [0,1] from strikeline → fade.
                float xNorm = note.Fret >= 0 && note.Fret < 5
                    ? 0.2f * note.Fret + 0.1f   // matches TrackElement.GetElementX(fret,5)
                    : 0.5f;                     // open / wildcard → center
                float yNorm = (float) (dt * ns / zRange);

                var screen = _highwayRenderer.GetTrackPositionScreenSpace(
                    _highwayIndex, xNorm, Mathf.Clamp01(yNorm));
                if (screen == null) continue;

                // Look up complexity values at this note's time.
                int idx = SampleIndex(note.Time, sampleCount);
                if (idx < 0) continue;
                float fretVal  = _analysis.Fret.Values[idx];
                float strumVal = _analysis.Strum.Values[idx];

                // HighwayCameraRendering.ViewportToScreen already returns
                // top-left origin: (1 - viewportY) * Screen.height. So we use
                // Y as-is — DON'T flip again.
                float guiX = screen.Value.x - NOTE_LABEL_W * 0.5f;
                float guiY = screen.Value.y - NOTE_LABEL_Y_OFFSET_PX - NOTE_LABEL_H;

                GUI.DrawTexture(new Rect(guiX, guiY, NOTE_LABEL_W, NOTE_LABEL_H), _texBg);
                GUI.Label(
                    new Rect(guiX, guiY, NOTE_LABEL_W, NOTE_LABEL_H),
                    $"F {fretVal:F1}\nS {strumVal:F1}",
                    _smallLabelStyle);
            }
        }

        private int SampleIndex(double t, int valueCount)
        {
            double offset = (t - _analysis.Fret.StartTimeSeconds) * _analysis.Fret.SampleRateHz;
            return Mathf.Clamp((int) offset, 0, valueCount - 1);
        }

        private void OnDestroy()
        {
            if (_texFret  != null) Destroy(_texFret);
            if (_texStrum != null) Destroy(_texStrum);
            if (_texBg    != null) Destroy(_texBg);
        }
    }
}
