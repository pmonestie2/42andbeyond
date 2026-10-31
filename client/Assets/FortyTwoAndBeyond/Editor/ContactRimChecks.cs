using System;
using System.Reflection;
using FortyTwoAndBeyond.Client;
using FortyTwoAndBeyond.Protocol;
using UnityEditor;
using UnityEngine;

namespace FortyTwoAndBeyond.Editor
{
    /// <summary>Check edge direction, reserved controls, dense selection and category gates.</summary>
    public static class ContactRimChecks
    {
        /// <summary>Run isolated geometry checks without a game account or server.</summary>
        public static void Run()
        {
            try
            {
                Type type = typeof(FlightHud);
                MethodInfo behind = type.GetMethod("BehindDirection", BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo anchor = type.GetMethod("RimAnchor", BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo clear = type.GetMethod("RimPositionClear", BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo next = type.GetMethod("NextClusterIndex", BindingFlags.NonPublic | BindingFlags.Static);
                Vector2 bearing = (Vector2)behind.Invoke(null, new object[] { new Vector3(0, 0, -20) });
                object[] args = { new Vector2(640, 360), bearing, 1280f, 720f, null };
                Vector2 point = (Vector2)anchor.Invoke(null, args);
                Require(point.y > 680 && args[4].ToString() == "Bottom",
                    "A directly behind-camera target must appear on the lower edge.");
                args = new object[] { new Vector2(640, 360), new Vector2(1, 0), 1280f, 720f, null };
                point = (Vector2)anchor.Invoke(null, args);
                Require(point.x > 1240 && args[4].ToString() == "Right",
                    "A rightward contact must appear on the right edge.");
                var footprint = new Rect(100, 20, 90, 32);
                var controls = new[] { new Rect(90, 10, 150, 150) };
                Require(!(bool)clear.Invoke(null, new object[] { footprint, controls, Array.Empty<Rect>() }),
                    "An edge cue may not cover the communications or contact panels.");
                Require((bool)clear.Invoke(null, new object[] { footprint,
                    new[] { new Rect(500, 100, 120, 80) }, Array.Empty<Rect>() }),
                    "A free rim slot was incorrectly rejected.");
                Require((int)next.Invoke(null, new object[] { new uint[] { 11, 12, 13 }, 12u }) == 2 &&
                    (int)next.Invoke(null, new object[] { new uint[] { 11, 12, 13 }, 13u }) == 0,
                    "Repeated cluster clicks must cycle to every member.");
                var hostile = new ShipActor(1, 0, 1, "Hostile", new Position(0, 0, 0),
                    new float[] { 0, 0, 0, 1 }, true, isPlayer: false, hostile: true);
                var neutral = new ShipActor(2, 0, 1, "Neutral", new Position(0, 0, 0),
                    new float[] { 0, 0, 0, 1 }, true, isPlayer: false, hostile: false);
                Require(ContactFiltering.Matches(ContactCategoryFilter.Combat, hostile) &&
                    !ContactFiltering.Matches(ContactCategoryFilter.Combat, neutral) &&
                    ContactFiltering.Matches(ContactCategoryFilter.All, neutral),
                    "Fight and All filters misclassified server actors.");
                Debug.Log("PASS: rim behind-camera bearing, card avoidance, cluster cycling and actor filters.");
                EditorApplication.Exit(0);
            }
            catch (Exception error)
            { Debug.LogException(error); EditorApplication.Exit(1); }
        }

        /// <summary>Reject one concrete edge-contact regression.</summary>
        private static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
    }
}
