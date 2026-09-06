using System.Collections.Generic;
using UnityEngine;

namespace ComputeTransparency
{
    /// <summary>
    /// Everything the compute rasterizer should draw: explicitly created
    /// <see cref="CTSpriteBatch"/>es, plus one internal batch holding the sprites contributed by
    /// <see cref="ComputeTransparentSprite"/> components.
    /// </summary>
    public static class CTSpriteRegistry
    {
        static readonly List<CTSpriteBatch> s_Batches = new List<CTSpriteBatch>();
        static readonly List<ComputeTransparentSprite> s_Components = new List<ComputeTransparentSprite>();

        static CTSpriteBatch s_ComponentBatch;

        public static IReadOnlyList<CTSpriteBatch> Batches => s_Batches;

        /// <summary>
        /// Total instances across every enabled batch. The component backed batch is refreshed
        /// once per frame by the renderer, so between a component being disabled and the next
        /// frame this still counts it.
        /// </summary>
        public static int Count
        {
            get
            {
                int total = 0;
                for (int i = 0; i < s_Batches.Count; i++)
                    if (s_Batches[i].Enabled)
                        total += s_Batches[i].Count;
                return total;
            }
        }

        internal static void AddBatch(CTSpriteBatch batch)
        {
            if (!s_Batches.Contains(batch))
                s_Batches.Add(batch);
        }

        internal static void RemoveBatch(CTSpriteBatch batch)
        {
            s_Batches.Remove(batch);
            if (ReferenceEquals(batch, s_ComponentBatch))
                s_ComponentBatch = null;
        }

        // Register and Unregister are driven by OnEnable and OnDisable, which Unity pairs, so no
        // duplicate check is needed. Guarding with List.Contains made building a scene of a few
        // thousand sprites quadratic.
        public static void Register(ComputeTransparentSprite sprite) => s_Components.Add(sprite);

        public static void Unregister(ComputeTransparentSprite sprite) => s_Components.Remove(sprite);

        /// <summary>
        /// Refreshes the batch backing the component path. Every component costs a native
        /// transform read here, which is exactly what the batch API exists to avoid; components
        /// are the convenient path, not the fast one.
        /// </summary>
        internal static void SyncComponents()
        {
            if (s_Components.Count == 0)
            {
                if (s_ComponentBatch != null && s_ComponentBatch.Count != 0)
                    s_ComponentBatch.Count = 0;
                return;
            }

            if (s_ComponentBatch == null)
                s_ComponentBatch = new CTSpriteBatch(Mathf.NextPowerOfTwo(s_Components.Count), "CT Components");
            else
                s_ComponentBatch.EnsureCapacity(s_Components.Count);

            var instances = s_ComponentBatch.Instances;
            int count = 0;
            for (int i = 0; i < s_Components.Count; i++)
            {
                var sprite = s_Components[i];
                if (sprite == null || !sprite.isActiveAndEnabled)
                    continue;

                instances[count] = sprite.ToInstance();
                count++;
            }

            s_ComponentBatch.Count = count;
            // Transforms may have moved since last frame and there is no cheap way to know, so
            // the component batch is always considered dirty.
            s_ComponentBatch.MarkChanged();
        }
    }
}
