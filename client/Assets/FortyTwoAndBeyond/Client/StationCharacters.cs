using System.Collections.Generic;
using System.Linq;
using FortyTwoAndBeyond.Protocol;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Present contacts from every native station room in one client concourse.</summary>
    public sealed class StationCharacters
    {
        private readonly SpaceArt art;
        private readonly Transform parent;
        private readonly Dictionary<uint, Transform> contacts = new Dictionary<uint, Transform>();
        private static readonly float[] ContactLanes = { -7f, -4.7f, 4.7f, 7f };

        /// <summary>Bind reusable art and the client scene root.</summary>
        public StationCharacters(SpaceArt art, Transform parent)
        { this.art = art; this.parent = parent; }

        /// <summary>Show all advertised contacts in stable positions without changing their native room IDs.</summary>
        public void Apply(StationState station)
        {
            StationContact[] visible = station?.Contacts
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
                    body.localRotation = Quaternion.Euler(0, ContactLanes[index % ContactLanes.Length] > 0 ? -72f : 72f, 0);
                }
                int row = index / ContactLanes.Length;
                body.localPosition = new Vector3(ContactLanes[index % ContactLanes.Length], 0, -5.5f + row * 1.65f);
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
