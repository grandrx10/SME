using System.Collections.Generic;
using UnityEngine;

namespace SuddenMageEncounter
{
    /// <summary>
    /// Where players start a round. Put several inside each arena prefab (Sudden Mage Encounter > Add Spawn Point);
    /// at round start every player gets a different one, spread as far apart as possible.
    /// </summary>
    [AddComponentMenu("Sudden Mage Encounter/Spawn Point")]
    public class SpawnPoint : MonoBehaviour
    {
        /// <summary>Who starts here: anyone (free-for-all), or one team in a Team Deathmatch.</summary>
        public enum For : byte { FreeForAll = 0, TeamA = 1, TeamB = 2, Solo = 3 }

        static readonly List<SpawnPoint> Loose = new();

        [Tooltip("Free For All: normal rounds. Team A / Team B / Solo: Team Deathmatch only (Solo: the odd player out). " +
                 "A map needs at least one A and one B to have Team Deathmatch, and a Solo when the number of players is odd.")]
        public For team = For.FreeForAll;

        [Tooltip("Random horizontal offset around this point (metres). Keep small so you don't spawn in walls.")]
        public float randomRadius = 0.3f;

        void OnEnable() => Loose.Add(this);
        void OnDisable() => Loose.Remove(this);

        public struct Spot { public Vector3 position; public float yaw; }

        static List<SpawnPoint> Candidates(For team = For.FreeForAll)
        {
            var list = new List<SpawnPoint>();
            var arena = Arena.Current;
            if (arena != null) foreach (var s in arena.Spawns) if (s != null && s.isActiveAndEnabled && s.team == team) list.Add(s);
            if (list.Count == 0 && team == For.FreeForAll) foreach (var s in Loose) if (s != null && s.team == team) list.Add(s);
            return list;
        }

        /// <summary>The current arena has the spawns a Team Deathmatch needs (with an odd player out or not).</summary>
        public static bool HasTeams(bool solo) =>
            Candidates(For.TeamA).Count > 0 && Candidates(For.TeamB).Count > 0 && (!solo || Candidates(For.Solo).Count > 0);

        /// <summary>Any spawn (client-side placeholder before the server places you).</summary>
        public static void Pick(out Vector3 position, out float yaw)
        {
            var list = Candidates();
            if (list.Count == 0) { position = new Vector3(0, 3, 0); yaw = 0; return; }
            var s = list[Random.Range(0, list.Count)];
            position = s.transform.position;
            yaw = s.transform.eulerAngles.y;
        }

        /// <summary>Server: one spot per player, as spread out as possible (greedy farthest-point).</summary>
        public static List<Spot> Assign(int count, For team = For.FreeForAll)
        {
            var result = new List<Spot>();
            var list = Candidates(team);
            if (list.Count == 0)
            {
                // no spawn points: a ring around the middle of the arena
                Vector3 c = Arena.Current != null ? Arena.Current.transform.position : Vector3.zero;
                for (int i = 0; i < count; i++)
                {
                    float a = i * Mathf.PI * 2f / Mathf.Max(1, count);
                    Vector3 p = c + new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * 8f;
                    p = Ground(p);
                    result.Add(new Spot { position = p, yaw = Quaternion.LookRotation(c - p).eulerAngles.y });
                }
                return result;
            }

            var chosen = new List<Vector3>();
            var free = new List<SpawnPoint>(list);
            for (int i = 0; i < count; i++)
            {
                if (free.Count == 0) free.AddRange(list); // more players than spawns: share (with offsets)
                SpawnPoint best = null;
                float bestScore = -1f;
                foreach (var s in free)
                {
                    float nearest = float.MaxValue;
                    foreach (var c in chosen) nearest = Mathf.Min(nearest, Vector3.Distance(c, s.transform.position));
                    float score = (chosen.Count == 0 ? Random.value : nearest) + Random.value * 0.5f;
                    if (score > bestScore) { bestScore = score; best = s; }
                }
                free.Remove(best);
                Vector2 r = Random.insideUnitCircle * best.randomRadius;
                if (chosen.Exists(c => (c - best.transform.position).sqrMagnitude < 0.25f)) r = Random.insideUnitCircle.normalized * 1.2f;
                Vector3 pos = best.transform.position + new Vector3(r.x, 0, r.y);
                chosen.Add(pos);
                result.Add(new Spot { position = pos, yaw = best.transform.eulerAngles.y });
            }
            return result;
        }

        static Vector3 Ground(Vector3 p)
        {
            if (Physics.Raycast(p + Vector3.up * 50f, Vector3.down, out var hit, 200f, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore))
                return hit.point + Vector3.up * 1f;
            return p + Vector3.up * 2f;
        }

        void OnDrawGizmos()
        {
            Color c = team switch
            {
                For.TeamA => new Color(1f, 0.6f, 0.1f),
                For.TeamB => new Color(0.2f, 0.6f, 1f),
                For.Solo => new Color(0.9f, 0.3f, 1f),
                _ => new Color(0.2f, 1f, 0.4f),
            };
            c.a = 0.8f;
            Gizmos.color = c;
            Gizmos.DrawWireSphere(transform.position, 0.4f);
            Gizmos.DrawLine(transform.position, transform.position + transform.forward);
            c.a = 0.25f;
            Gizmos.color = c;
            Gizmos.DrawWireCube(transform.position, new Vector3(0.7f, 1.95f, 0.7f));
        }
    }
}
