using System.Collections.Generic;
using System.Linq;
using FortyTwoAndBeyond.Protocol;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Keep static contact suits aligned with the authoritative station room.</summary>
    public sealed class StationCharacters
    {
        private readonly SpaceArt art;
        private readonly Transform parent;
        private readonly Dictionary<uint, Transform> contacts = new Dictionary<uint, Transform>();

        /// <summary>Bind reusable art and the client scene root.</summary>
        public StationCharacters(SpaceArt art, Transform parent)
        { this.art = art; this.parent = parent; }

        /// <summary>Show one suit for each contact published in the current room.</summary>
        public void Apply(StationState station)
        {
            uint room = station?.CurrentRoom ?? uint.MaxValue;
            StationContact[] visible = station?.Contacts.Where(contact => contact.Room == room)
                .OrderBy(contact => contact.Id).ToArray() ?? System.Array.Empty<StationContact>();
            var ids = new HashSet<uint>();
            for (int index = 0; index < visible.Length; index++)
            {
                uint id = visible[index].Id;
                ids.Add(id);
                if (!contacts.TryGetValue(id, out Transform body))
                {
                    body = art.StationAvatar(parent, contact: true);
                    body.name = "Station contact " + id;
                    contacts[id] = body;
                    body.localRotation = Quaternion.Euler(0, index % 2 == 0 ? -72f : 72f, 0);
                }
                int side = index % 2 == 0 ? 1 : -1;
                int row = index / 2;
                float lateral = room == 0 ? 6.5f : 4.7f;
                float depth = room == 0 ? -2f + row * 3f : 3.5f + row * 2.6f;
                body.localPosition = new Vector3(side * lateral, 0, Mathf.Clamp(depth, -7f, 8f));
            }
            foreach (uint id in contacts.Keys.Where(id => !ids.Contains(id)).ToArray())
            { Object.Destroy(contacts[id].gameObject); contacts.Remove(id); }
        }

        /// <summary>Turn only the contacted NPC toward the pilot during server dialogue.</summary>
        public void Tick(Vector3 pilotPosition, uint dialogueNpcId, float delta)
        {
            float blend = 1f - Mathf.Exp(-3f * Mathf.Min(delta, .1f));
            foreach (KeyValuePair<uint, Transform> entry in contacts)
            {
                Transform body = entry.Value;
                bool engaged = dialogueNpcId != 0 && entry.Key == dialogueNpcId;
                body.GetComponent<StationSuitMotion>()?.SetEngaged(engaged);
                Quaternion idle = Quaternion.Euler(0, body.localPosition.x > 0 ? -72f : 72f, 0);
                Vector3 direction = pilotPosition - body.localPosition;
                direction.y = 0;
                Quaternion target = engaged && direction.sqrMagnitude > .1f ?
                    Quaternion.LookRotation(direction, Vector3.up) : idle;
                body.localRotation = Quaternion.Slerp(body.localRotation, target, blend);
            }
        }

        /// <summary>Remove contact suits when the pilot disconnects or leaves the sector.</summary>
        public void Clear()
        {
            foreach (Transform body in contacts.Values) Object.Destroy(body.gameObject);
            contacts.Clear();
        }
    }
}
