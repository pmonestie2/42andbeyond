using System;
using System.Collections;
using System.Reflection;
using FortyTwoAndBeyond.Client;
using FortyTwoAndBeyond.Protocol;
using UnityEditor;
using UnityEngine;

namespace FortyTwoAndBeyond.Editor
{
    /// <summary>Exercise current-frame marker and label hit precedence without a live client.</summary>
    public static class TacticalMapHitChecks
    {
        /// <summary>Run the opt-in map geometry regression in an owned Editor process.</summary>
        public static void Run()
        {
            try
            {
                var map = new TacticalSectorMap(null, null);
                Type mapType = typeof(TacticalSectorMap);
                Set(mapType, map, "plot", new Rect(0, 0, 400, 300));
                Set(mapType, map, "radiusKm", 40f);
                Set(mapType, map, "centerKm", Vector2.zero);
                Set(mapType, map, "selectedId", 1u);
                Set(mapType, map, "label", new GUIStyle { fontSize = 13, wordWrap = true });
                Type markerType = mapType.GetNestedType("Marker", BindingFlags.NonPublic);
                IList markers = (IList)mapType.GetField("markers", BindingFlags.Instance |
                    BindingFlags.NonPublic).GetValue(map);
                markers.Add(Marker(markerType, 1u, "Selected navigation", new Position(0, 0, 0)));
                MethodInfo rebuild = mapType.GetMethod("RebuildHitRegions", BindingFlags.Instance |
                    BindingFlags.NonPublic);
                MethodInfo hovered = mapType.GetMethod("Hovered", BindingFlags.Instance |
                    BindingFlags.NonPublic);
                rebuild.Invoke(map, null);
                Require(Id(markerType, hovered.Invoke(map, new object[] { new Vector2(179, 150) })) == 1,
                    "A visible glyph must be selectable within 22 pixels.");
                IList hits = (IList)mapType.GetField("labelHits", BindingFlags.Instance |
                    BindingFlags.NonPublic).GetValue(map);
                Require(hits.Count > 0, "Selected navigation label was not placed.");
                Rect firstLabel = ((ValueTuple<Rect, uint>)hits[0]).Item1;
                Vector2 labelCenter = firstLabel.center;
                float scale = 300f / 80f;
                markers.Add(Marker(markerType, 2u, "Nearby marker", new Position(
                    (150 - labelCenter.y) / scale * 1000,
                    (labelCenter.x - 200) / scale * 1000, 0)));
                rebuild.Invoke(map, null);
                Require(Id(markerType, hovered.Invoke(map, new object[] { labelCenter })) == 1,
                    "A visible label must win over a different glyph beneath it.");
                Debug.Log("PASS: tactical map current-frame labels and larger marker hit targets.");
                EditorApplication.Exit(0);
            }
            catch (Exception error)
            {
                Debug.LogException(error);
                EditorApplication.Exit(1);
            }
        }

        /// <summary>Set one private field in an isolated map fixture.</summary>
        private static void Set(Type type, object target, string field, object value) =>
            type.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);

        /// Returns:
        ///     object: A private marker initialized from authoritative-style map coordinates.
        private static object Marker(Type type, uint id, string name, Position position)
        {
            object marker = Activator.CreateInstance(type, true);
            type.GetField("Id").SetValue(marker, id);
            type.GetField("Name").SetValue(marker, name);
            type.GetField("Position").SetValue(marker, position);
            return marker;
        }

        /// Returns:
        ///     uint: The ID selected by a reflected marker hit, or zero.
        private static uint Id(Type type, object marker) => marker == null ? 0 :
            (uint)type.GetField("Id").GetValue(marker);

        /// <summary>Reject a specific navigation-hit regression.</summary>
        private static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
    }
}
