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

        // Per-chunk-shape note label backgrounds. Free notes (anchors between
        // recognized motion chunks) render in flat gray; the four pattern shapes
        // each get a distinct hue.
        private static readonly Color ChunkColorFree    = new(0.45f, 0.45f, 0.45f, 0.85f);
        private static readonly Color ChunkColorTrill   = new(0.95f, 0.30f, 0.30f, 0.85f);
        private static readonly Color ChunkColorRollOn  = new(0.30f, 0.85f, 0.40f, 0.85f);
        private static readonly Color ChunkColorRollOff = new(0.30f, 0.75f, 0.95f, 0.85f);
        private static readonly Color ChunkColorZig     = new(0.95f, 0.65f, 0.20f, 0.85f);

        private TrackPlayer            _trackPlayer;
        private GameManager            _gameManager;
        private HighwayCameraRendering _highwayRenderer;
        private DifficultyAnalysis     _analysis;
        private List<GuitarNote>       _notes;
        private int                    _highwayIndex;

        // Per-note chunk-shape lookup, sized to _notes.Count. Built once at
        // Initialize from _analysis.FretChunks. Notes outside any chunk default
        // to Free.
        private DifficultyChunkShape[] _noteChunkShape;
        // Per-note flag: true if this note is the first note of a chunk (used
        // to draw transition lines).
        private bool[]                 _noteIsChunkStart;
        // Per-note flag: true if this note's chunk is part of a repeat run.
        private bool[]                 _noteChunkInRepeat;
        // Per-note flag: true when the note sits at its enclosing chunk's
        // lowest fret. Sourced directly from DifficultyAnalysis.NoteIsAnchor.
        // Anchors render in gray regardless of chunk shape.
        private bool[]                 _noteIsAnchor;
        // Per-note chunk index (which entry in _analysis.FretChunks contains
        // this note). -1 for any note not covered by a chunk. Currently kept
        // for debugging / future use; hue alternation is driven by the
        // per-shape parity bit below.
        private int[]                  _noteChunkIndex;
        // Per-note alternation bit: 0 or 1, flipping each time a new chunk of
        // the same shape appears in the chart. Drives the 2-state hue
        // alternation so consecutive same-shape chunks read as "same pattern,
        // different occurrence" rather than as a flat block of one color or a
        // random sprinkle.
        private byte[]                 _noteShapePhase;
        // Reverse lookup keyed by note Tick (uint) so external callers can
        // resolve a chart note → its chunk index in O(1). We *cannot* key by
        // reference: FiveFretGuitarPlayer.GetNotes calls
        // <c>chart.GetFiveFretTrack(...).Clone()</c>, so every spawned note is
        // a deep copy of the chart instance the visualizer holds. Tick is
        // preserved through Clone, unique per chord parent, and shared with
        // all chord-child notes (chord members are simultaneous), so it
        // identifies a chunk slot reliably regardless of which clone instance
        // shows up at PaintNote/UpdateColor time.
        private Dictionary<uint, int> _noteIndexByTick;

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
        // Per-chunk-shape label backgrounds, indexed by (int)DifficultyChunkShape.
        private Texture2D[] _texChunkShape;
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

            // The TrackPlayer exposes the renderer directly via a public
            // accessor. Falling back to GetComponentInChildren if for some
            // reason the inspector reference isn't wired (older scenes, etc.).
            _highwayRenderer = trackPlayer.HighwayRenderer
                ?? trackPlayer.GetComponentInChildren<HighwayCameraRendering>(includeInactive: true);
            if (_highwayRenderer == null)
            {
                YARG.Core.Logging.YargLogger.LogWarning(
                    "DifficultyVisualizer: HighwayCameraRendering not found — per-note labels and chunk-transition lines will fall back.");
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

            BuildChunkLookup();
        }

        // Materialize the per-note chunk metadata into flat arrays keyed by
        // note index, so the per-frame draw loop is a O(1) lookup instead of
        // a chunk-list scan. The Ghpp converter is order-preserving, so chunk
        // indices map 1-1 onto _notes.
        private void BuildChunkLookup()
        {
            int n = _notes.Count;
            _noteChunkShape    = new DifficultyChunkShape[n];
            _noteIsChunkStart  = new bool[n];
            _noteChunkInRepeat = new bool[n];
            _noteIsAnchor      = new bool[n];
            _noteChunkIndex    = new int[n];
            _noteShapePhase    = new byte[n];
            _noteIndexByTick   = new Dictionary<uint, int>(n);

            // Source the per-note anchor mask from the analysis. Length should
            // equal _notes.Count; if not we treat anything past the mask as
            // non-anchor (safe — pattern color path still applies).
            var anchorMask = _analysis.NoteIsAnchor;
            int anchorMaskLen = anchorMask?.Count ?? 0;

            // Default shape is Free for any note not covered by a chunk. We
            // only need to record the parent's tick — chord children share the
            // same tick, so any sibling that arrives at lookup time still
            // resolves correctly.
            for (int i = 0; i < n; i++)
            {
                _noteChunkShape[i] = DifficultyChunkShape.Free;
                _noteChunkIndex[i] = -1;
                _noteIsAnchor[i]   = i < anchorMaskLen && anchorMask[i];
                _noteIndexByTick[_notes[i].Tick] = i;
            }

            var chunks = _analysis.FretChunks;

            // Per-shape occurrence counter, indexed by (int)DifficultyChunkShape.
            // Each new chunk of a given shape increments its counter; the LSB
            // becomes the chunk's alternation phase (0 or 1).
            var perShapeOccurrences = new int[8];

            if (chunks != null)
            {
                for (int c = 0; c < chunks.Count; c++)
                {
                    var chunk = chunks[c];
                    int start = chunk.StartNoteIndex;
                    int end   = chunk.EndNoteIndex;
                    if (start < 0 || end >= n || start > end) continue;

                    int shapeIdx = (int) chunk.Shape;
                    byte phase = (byte) (perShapeOccurrences[shapeIdx] & 1);
                    perShapeOccurrences[shapeIdx]++;

                    for (int j = start; j <= end; j++)
                    {
                        _noteChunkShape[j]    = chunk.Shape;
                        _noteChunkInRepeat[j] = chunk.InRepeat;
                        _noteChunkIndex[j]    = c;
                        _noteShapePhase[j]    = phase;
                    }
                    _noteIsChunkStart[start] = true;
                }
            }
        }

        /// <summary>
        /// Build a list of synthetic Beatlines positioned at the start time of
        /// every chunk that marks a real pattern transition. The intent is to
        /// inject these into the host TrackPlayer's beatline list so the
        /// existing <c>BeatlineElement</c> pool/pipeline renders them as
        /// bright vertical lines on the highway — same code path that draws
        /// measure bars, nothing custom.
        ///
        /// A chunk is treated as a transition only when:
        ///   - it has a recognized motion shape (Trill/RollOn/RollOff/Zig);
        ///     Free and Held chunks aren't patterns, so they don't transition.
        ///   - it is NOT part of an ongoing K-period repeat — i.e. repeating
        ///     the same Zig four times only emits ONE transition line at the
        ///     first Zig, not four. The K-period detector inside the GHPP
        ///     chunker tags chunks past the first repeat occurrence with
        ///     InRepeat=true.
        /// </summary>
        public List<Beatline> BuildChunkTransitionBeatlines()
        {
            var result = new List<Beatline>();
            if (_analysis == null || _notes == null) return result;

            var chunks = _analysis.FretChunks;
            if (chunks == null) return result;

            for (int c = 0; c < chunks.Count; c++)
            {
                var chunk = chunks[c];
                if (chunk.Shape == DifficultyChunkShape.Free) continue;
                if (chunk.Shape == DifficultyChunkShape.Held) continue;
                if (chunk.InRepeat) continue;

                int idx = chunk.StartNoteIndex;
                if (idx < 0 || idx >= _notes.Count) continue;

                var note = _notes[idx];
                // Use BeatlineType.Measure so the line gets the brightest /
                // tallest visual treatment from BeatlineElement (yScale=0.07,
                // alpha=0.6) — matches what the user asked for.
                result.Add(new Beatline(BeatlineType.Measure, note.Time, note.Tick));
            }

            return result;
        }

        /// <summary>
        /// Resolve a chart note to the color of its enclosing fret-chunk shape.
        /// Returns false if the visualizer hasn't initialized chunk data, or if
        /// the supplied note isn't part of this player's chart (e.g. wrong
        /// player in multiplayer). Free chunks (notes that fall between
        /// recognized motion patterns) return the gray "anchor" color.
        /// </summary>
        public bool TryGetChunkColor(GuitarNote note, out Color color)
        {
            color = default;
            if (note == null || _noteIndexByTick == null) return false;
            if (!_noteIndexByTick.TryGetValue(note.Tick, out int idx)) return false;

            // Anchor notes (lowest fret in their chunk) always render gray
            // regardless of the enclosing chunk's shape — they're the
            // structural pivot of the motion, not the colored "target" notes.
            // This includes 1-note Free chunks: the single isolated note is
            // its own anchor, so it stays gray.
            if (_noteIsAnchor[idx])
            {
                var anchor = ChunkColorFree;
                anchor.a = 1f;
                color = anchor;
                return true;
            }

            // Non-anchor notes get the chunk-shape color, alternating between
            // two hues per consecutive same-shape chunk. The shape constants
            // are tuned for translucent OnGUI label backgrounds (alpha 0.85);
            // SetColorWithEmission treats alpha as a body multiplier on the
            // highway, so we force alpha 1 here.
            var baseColor = ColorForShape(_noteChunkShape[idx]);
            baseColor.a = 1f;
            color = _noteShapePhase[idx] == 0 ? baseColor : PerturbHue(baseColor);
            return true;
        }

        /// <summary>
        /// Returns the "alternate" variant of the given shape's base color.
        /// Used by every odd-occurrence chunk of a given shape so consecutive
        /// same-shape chunks form a deterministic A/B/A/B pattern rather than
        /// a flat block of one color or a random sprinkle of variants.
        /// </summary>
        private static Color PerturbHue(Color baseColor)
        {
            Color.RGBToHSV(baseColor, out float h, out float s, out float v);
            // ~+15° hue shift on the unit circle (+0.04 of 1.0). Big enough
            // to read as distinct, small enough that a Trill still reads as
            // the Trill color.
            h = Mathf.Repeat(h + 0.04f, 1f);
            // Slight value bump so the alternate variant doesn't just look
            // like a duller version — gives it a clearly "different" feel.
            v = Mathf.Clamp01(v * 0.88f);

            var result = Color.HSVToRGB(h, s, v);
            result.a = baseColor.a;
            return result;
        }

        private static Color ColorForShape(DifficultyChunkShape shape)
        {
            switch (shape)
            {
                case DifficultyChunkShape.Trill:   return ChunkColorTrill;
                case DifficultyChunkShape.RollOn:  return ChunkColorRollOn;
                case DifficultyChunkShape.RollOff: return ChunkColorRollOff;
                case DifficultyChunkShape.Zig:     return ChunkColorZig;
                default:                           return ChunkColorFree;
            }
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

            // One swatch per DifficultyChunkShape value, ordered to match the
            // enum so we can index by (int)shape.
            _texChunkShape = new Texture2D[5];
            _texChunkShape[(int) DifficultyChunkShape.Free]    = MakeTex(ChunkColorFree);
            _texChunkShape[(int) DifficultyChunkShape.Trill]   = MakeTex(ChunkColorTrill);
            _texChunkShape[(int) DifficultyChunkShape.RollOn]  = MakeTex(ChunkColorRollOn);
            _texChunkShape[(int) DifficultyChunkShape.RollOff] = MakeTex(ChunkColorRollOff);
            _texChunkShape[(int) DifficultyChunkShape.Zig]     = MakeTex(ChunkColorZig);

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
                float yClamped = Mathf.Clamp01(yNorm);

                var screen = _highwayRenderer.GetTrackPositionScreenSpace(
                    _highwayIndex, xNorm, yClamped);
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

                // Pick the label background by chunk shape — gray for Free
                // (anchor) notes, distinct hue per pattern shape.
                var shape = (i < _noteChunkShape.Length)
                    ? _noteChunkShape[i]
                    : DifficultyChunkShape.Free;
                var swatch = _texChunkShape[(int) shape];

                GUI.DrawTexture(new Rect(guiX, guiY, NOTE_LABEL_W, NOTE_LABEL_H), swatch);
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

            if (_texChunkShape != null)
            {
                for (int i = 0; i < _texChunkShape.Length; i++)
                {
                    if (_texChunkShape[i] != null) Destroy(_texChunkShape[i]);
                }
            }
        }
    }
}
