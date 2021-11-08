﻿using System.Collections.ObjectModel;
using System.Collections.Generic;

namespace SNet_Client.PacketCouriers.Entities
{
    public class NetworkedEntityDictionary  : KeyedCollection<int, NetworkedEntity>
    {
        protected override int GetKeyForItem(NetworkedEntity entity) => entity.id;
        public bool TryGetValue(int key, out NetworkedEntity value)
        {
            value = null;
            if (Contains(key))
            {
                value = this[key];
                return true;
            }
            return false;
        }
    }
    public class NetworkedEntityOwnerDictionary : Dictionary<int, NetworkedEntityDictionary>
    {

        public int GetTotalEntityCount()
        {
            int count = 0;
            foreach (var val in Values)
                count += val.Count;
            return count;
        }  

        public NetworkedEntity GetNetworkedEntity(int ownerID, int id)
        {
            return this[ownerID][id];
        }
        public bool TryGetNetworkedEntity(int ownerID, int id, out NetworkedEntity entity)
        {
            return this[ownerID].TryGetValue(id, out entity);
        }

        public bool ContainsEntity(NetworkedEntity networkedEntity)
        {
            if (!TryGetValue(networkedEntity.ownerId, out var entities))
                return false;

            return entities.Contains(networkedEntity);
        }

        public void RemoveEntity(NetworkedEntity networkedEntity)
        {
            if (!TryGetValue(networkedEntity.ownerId, out var entities))
                return;

            entities.Remove(networkedEntity);
        }

        public void ClearAndDestroyAllEntites()
        {
            foreach (var entities in Values)
            {
                for (int i = 0; i < entities.Count; i++)
                    UnityEngine.Object.Destroy(entities[i].gameObject);
            }
            Clear();
        }

        public void RemoveAndDestroyOwnerIDEntites(int ownerID)
        {
            if (!TryGetValue(ownerID, out var entities))
                return;

            for (int i = 0; i < entities.Count; i++)
                UnityEngine.Object.Destroy(entities[i].gameObject);

            Remove(ownerID);
        }

        public void AddEntity(NetworkedEntity networkedEntity)
        {
            if (!TryGetValue(networkedEntity.ownerId, out NetworkedEntityDictionary entities))
            {
                entities = new NetworkedEntityDictionary();
                Add(networkedEntity.ownerId, entities);
            }

            entities.Add(networkedEntity);
        }
    }
}