using System;
using System.Reflection;
using FortyTwoAndBeyond.Client;
using UnityEngine;

namespace FortyTwoAndBeyond.Editor
{
    /// <summary>Exercise the live map shortcut handler without networking or user state.</summary>
    public static class NavigationShortcutChecks
    {
        /// <summary>Verify repeated Tab toggles and dismissal while foreground input is blocked.</summary>
        public static void Run()
        {
            var fixture = new GameObject("Disposable navigation shortcut checks");
            fixture.SetActive(false);
            try
            {
                SpaceClient client = fixture.AddComponent<SpaceClient>();
                MethodInfo handler = typeof(SpaceClient).GetMethod("HandleChartShortcut",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                handler.Invoke(client, new object[] { true, false });
                Require(client.Chart, "First Tab must open the map.");
                handler.Invoke(client, new object[] { true, false });
                Require(!client.Chart, "Second Tab must close the map.");
                handler.Invoke(client, new object[] { true, true });
                Require(!client.Chart, "Typing must not open a hidden map.");
                client.SetChart(true);
                handler.Invoke(client, new object[] { false, true });
                Require(client.Chart, "Blocked input alone must not close the map.");
                handler.Invoke(client, new object[] { true, true });
                Require(!client.Chart, "Tab must dismiss the map despite foreground input focus.");
                handler.Invoke(client, new object[] { false, false });
                Require(!client.Chart, "Releasing Tab must not reopen the map.");
                Debug.Log("PASS: map shortcut open/close, blocked-input dismissal, typing guard and key release.");
            }
            finally { UnityEngine.Object.DestroyImmediate(fixture); }
        }

        /// <summary>Fail the batch validation when a navigation input invariant is broken.</summary>
        private static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
    }
}
