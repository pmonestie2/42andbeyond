using FortyTwoAndBeyond.Protocol;
using UnityEngine;
using UnityEngine.UIElements;

namespace FortyTwoAndBeyond.Client
{
    public sealed partial class FlightHud
    {
        private sealed partial class DeckView
        {
            private readonly ExperienceTrack[] experienceTracks = new ExperienceTrack[3];
            private ExperienceTrack hoveredExperience;

            /// <summary>Place the three native experience tracks immediately below the resource dials.</summary>
            private void BuildExperienceTracks(VisualElement instruments)
            {
                var tracks = Box(instruments, "experience-progress");
                tracks.name = "experience-progress";
                Place(tracks, 0, 87, 202, 54);
                experienceTracks[0] = new ExperienceTrack(tracks, "combat", "Combat", new Color(1f, .53f, .39f));
                experienceTracks[1] = new ExperienceTrack(tracks, "explore", "Explore", new Color(.34f, .84f, .87f));
                experienceTracks[2] = new ExperienceTrack(tracks, "trade", "Trade", new Color(.94f, .73f, .36f));
            }

            /// <summary>Display server levels and fractions without estimating absolute experience totals.</summary>
            private void RefreshExperience(ExperienceState experience)
            {
                experienceTracks[0].Set(experience?.CombatLevel, experience?.CombatFraction);
                experienceTracks[1].Set(experience?.ExploreLevel, experience?.ExploreFraction);
                experienceTracks[2].Set(experience?.TradeLevel, experience?.TradeFraction);
                if (hoveredExperience == null) return;
                gaugeHoverText.text = hoveredExperience.Root.tooltip;
                if (hud.equipmentOpen)
                { hoveredExperience = null; gaugeHover.style.display = DisplayStyle.None; }
            }

            /// <summary>Show native progress details in the existing runtime resource tooltip.</summary>
            private void BindExperienceHover()
            {
                foreach (ExperienceTrack track in experienceTracks)
                {
                    track.Root.RegisterCallback<PointerEnterEvent>(evt => ShowExperienceHover(track, evt.position));
                    track.Root.RegisterCallback<PointerMoveEvent>(evt => ShowExperienceHover(track, evt.position));
                    track.Root.RegisterCallback<PointerLeaveEvent>(_ =>
                    {
                        if (hoveredExperience != track) return;
                        hoveredExperience = null; gaugeHover.style.display = DisplayStyle.None;
                    });
                }
            }

            /// <summary>Keep the experience tooltip beside the cursor and inside the viewport.</summary>
            private void ShowExperienceHover(ExperienceTrack track, Vector2 point)
            {
                hoveredGauge = null; hoveredExperience = track;
                gaugeHoverText.text = track.Root.tooltip;
                gaugeHover.style.left = Mathf.Clamp(point.x + 16, 4, Mathf.Max(4, root.resolvedStyle.width - 348));
                gaugeHover.style.top = Mathf.Clamp(point.y + 16, 4, Mathf.Max(4, root.resolvedStyle.height - 110));
                gaugeHover.style.display = DisplayStyle.Flex; gaugeHover.BringToFront();
            }

            /// <summary>A compact level label and calibrated progress rail for one experience track.</summary>
            private sealed class ExperienceTrack
            {
                public readonly VisualElement Root;
                private readonly Label caption, percent;
                private readonly VisualElement fill;
                private readonly string title;
                private bool initialized;
                private uint? previousLevel;
                private float? previousFraction;

                /// <summary>Create one retained experience row without per-frame geometry allocation.</summary>
                public ExperienceTrack(VisualElement parent, string key, string title, Color color)
                {
                    this.title = title;
                    Root = Box(parent, "experience-track"); Root.name = "experience-" + key;
                    caption = Text(Root, "", "experience-caption"); caption.style.color = color;
                    var rail = Box(Root, "experience-rail"); rail.name = Root.name + "-rail";
                    fill = Box(rail, "experience-fill"); fill.name = Root.name + "-fill";
                    fill.style.backgroundColor = color;
                    Box(fill, "experience-sheen");
                    var divisions = Box(rail, "experience-divisions");
                    for (int index = 0; index < 4; index++) Box(divisions, "experience-division");
                    percent = Text(Root, "", "experience-percent");
                    foreach (VisualElement child in Root.Children()) child.pickingMode = PickingMode.Ignore;
                    Set(null, null);
                }

                /// <summary>Update only changed telemetry and preserve a clear unknown state.</summary>
                public void Set(uint? level, float? fraction)
                {
                    if (initialized && previousLevel == level && previousFraction == fraction) return;
                    initialized = true; previousLevel = level; previousFraction = fraction;
                    bool known = level.HasValue && fraction.HasValue;
                    caption.text = title.ToUpperInvariant() + "  " + (known ? level.Value.ToString() : "—");
                    float progress = known ? Mathf.Clamp01(fraction.Value) : 0;
                    percent.text = known ? $"{progress * 100:0.0}%" : "—";
                    fill.style.width = Length.Percent(progress * 100);
                    Root.tooltip = known ? $"{title} · Level {level.Value}\nLevel progress: {progress * 100:0.###}%" :
                        $"Awaiting {title.ToLowerInvariant()} experience from the server.";
                }
            }
        }
    }
}
