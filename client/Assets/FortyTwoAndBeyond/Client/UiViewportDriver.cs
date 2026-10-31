#if UNITY_EDITOR
using System;
using UnityEngine;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Run viewport assertions where Screen reports the player render dimensions.</summary>
    [DefaultExecutionOrder(10000)]
    public sealed class UiViewportDriver : MonoBehaviour
    {
        public Action Tick, LateTick;
        /// <summary>Advance only the owned test's UI state machine.</summary>
        private void Update(){Tick?.Invoke();}
        /// <summary>Sample an owned flight fixture after its rendered pose and camera have advanced.</summary>
        private void LateUpdate(){LateTick?.Invoke();}
    }
}

#endif
