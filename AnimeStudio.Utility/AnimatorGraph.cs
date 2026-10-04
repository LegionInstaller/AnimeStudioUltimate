using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AnimeStudio
{
    /// <summary>
    /// The base layer of an Animator's controller as a small JSON file next to the FBX, so
    /// the Blender add-on can lay out a combo the way the game plays it. Times are turned
    /// into frames here, where it is known which of Unity's and ZZZ's fields hold the value.
    /// </summary>
    public static class AnimatorGraph
    {
        public const string Extension = ".animator.json";

        /// <summary>
        /// Writes "&lt;fbx name&gt;.animator.json" for an export. The animator's own controller is
        /// used when it has one. A _Model prefab has none (the controller sits on the playable
        /// prefab), and a merged export may have no animator at all; then the loaded controller
        /// that holds most of the exported clips is taken. False if nothing fits.
        /// </summary>
        public static bool TryWrite(Animator animator, string fbxPath, IEnumerable<string> exportedClips = null, SerializedFile loadedFrom = null)
        {
            RuntimeAnimatorController runtime = null;
            if (animator == null || !animator.m_Controller.TryGet(out runtime) || Resolve(runtime).controller == null)
                runtime = BestMatch(exportedClips, loadedFrom ?? animator?.assetsFile);
            var (controller, overrides) = Resolve(runtime);
            if (controller == null)
            {
                Logger.Info($"No animator graph for {Path.GetFileName(fbxPath)}: no loaded AnimatorController plays its clips");
                return false;
            }

            var graph = Build(controller, overrides);
            var path = Path.Combine(Path.GetDirectoryName(fbxPath) ?? "", Path.GetFileNameWithoutExtension(fbxPath) + Extension);
            File.WriteAllText(path, JsonConvert.SerializeObject(graph, Formatting.Indented));
            Logger.Info($"Animator graph of {runtime.m_Name} written to {Path.GetFileName(path)}");
            return true;
        }

        /// <summary>The base controller and, for an override controller, its clip replacements.</summary>
        private static (AnimatorController controller, Dictionary<AnimationClip, AnimationClip> overrides) Resolve(RuntimeAnimatorController runtime)
        {
            var overrides = new Dictionary<AnimationClip, AnimationClip>();
            var controller = runtime as AnimatorController;
            if (runtime is AnimatorOverrideController overrideController)
            {
                if (overrideController.m_Controller.TryGet(out var inner))
                    controller = inner as AnimatorController;
                foreach (var pair in overrideController.m_Clips)
                {
                    if (pair.m_OriginalClip.TryGet(out var original) && pair.m_OverrideClip.TryGet(out var replacement))
                        overrides[original] = replacement;
                }
            }
            if (controller?.m_Controller == null || controller.m_Controller.m_LayerArray.Count == 0)
                return (null, overrides);
            return (controller, overrides);
        }

        /// <summary>The loaded controller whose clips overlap most with the exported ones.</summary>
        private static RuntimeAnimatorController BestMatch(IEnumerable<string> exportedClips, SerializedFile loadedFrom)
        {
            var wanted = new HashSet<string>(exportedClips ?? Enumerable.Empty<string>());
            var files = loadedFrom?.assetsManager?.assetsFileList;
            if (wanted.Count == 0 || files == null)
                return null;

            RuntimeAnimatorController best = null;
            int bestHits = 0, bestSize = 0;
            foreach (var runtime in files.SelectMany(f => f.Objects).OfType<RuntimeAnimatorController>())
            {
                var (controller, overrides) = Resolve(runtime);
                if (controller == null)
                    continue;
                var names = controller.m_AnimationClips
                    .Select(p => p.TryGet(out var clip) ? (overrides.TryGetValue(clip, out var o) ? o : clip).m_Name : null)
                    .Where(n => n != null).Distinct().ToList();
                var hits = names.Count(wanted.Contains);
                // Most exported clips first; between equals, the one with the bigger graph.
                if (hits > bestHits || (hits == bestHits && hits > 0 && names.Count > bestSize))
                {
                    best = runtime;
                    bestHits = hits;
                    bestSize = names.Count;
                }
            }
            return best;
        }

        private static Dictionary<string, object> Build(AnimatorController controller, Dictionary<AnimationClip, AnimationClip> overrides)
        {
            var tos = controller.m_TOS ?? new Dictionary<uint, string>();
            string Name(uint id) => tos.TryGetValue(id, out var s) ? s : "#" + id;

            var clips = controller.m_AnimationClips.Select(pptr =>
            {
                if (!pptr.TryGet(out var clip))
                    return null;
                return overrides.TryGetValue(clip, out var replacement) ? replacement : clip;
            }).ToList();

            var cc = controller.m_Controller;
            var layer = cc.m_LayerArray[0];
            var machine = cc.m_StateMachineArray[(int)layer.m_StateMachineIndex];
            var rate = clips.Where(c => c != null && c.m_SampleRate > 0).Select(c => c.m_SampleRate).FirstOrDefault();
            if (rate <= 0)
                rate = 60;

            int Frames(AnimationClip clip)
            {
                var muscle = clip?.m_MuscleClip;
                if (muscle == null)
                    return 0;
                var r = clip.m_SampleRate > 0 ? clip.m_SampleRate : rate;
                return (int)Math.Round((muscle.m_StopTime - muscle.m_StartTime) * r);
            }

            // unloaded: the state plays a clip, but it was not among the loaded assets, so
            // its name cannot be known. Said out loud so a combo cannot skip it unnoticed.
            AnimationClip ClipOf(StateConstant state, out bool blended, out bool unloaded)
            {
                var leaves = state.m_BlendTreeConstantArray
                    .SelectMany(tree => tree.m_NodeArray)
                    .Where(node => node.m_ChildIndices == null || node.m_ChildIndices.Length == 0)
                    .ToList();
                blended = leaves.Count > 1;
                var leaf = leaves.FirstOrDefault();
                var clip = leaf != null && leaf.m_ClipID < clips.Count ? clips[(int)leaf.m_ClipID] : null;
                unloaded = leaf != null && clip == null;
                return clip;
            }

            var stateFrames = new List<int>();
            var states = new List<Dictionary<string, object>>();
            foreach (var state in machine.m_StateConstantArray)
            {
                var clip = ClipOf(state, out var blended, out var unloaded);
                stateFrames.Add(Frames(clip));
                states.Add(new Dictionary<string, object>
                {
                    ["name"] = Name(state.m_NameID),
                    ["path"] = Name(state.m_FullPathID),
                    ["clip"] = clip?.m_Name,
                    ["clipUnloaded"] = unloaded,
                    ["frames"] = stateFrames[^1],
                    ["loop"] = clip?.m_MuscleClip?.m_LoopTime ?? false,
                    ["speed"] = state.m_Speed,
                    ["blendTree"] = blended,
                });
            }

            Dictionary<string, object> Target(uint destination)
            {
                if (destination < machine.m_StateConstantArray.Count)
                    return new Dictionary<string, object> { ["state"] = (int)destination };
                if (destination >= 30000 && destination - 30000 < machine.m_SelectorStateConstantArray.Count)
                    return new Dictionary<string, object> { ["selector"] = (int)(destination - 30000) };
                return new Dictionary<string, object> { ["unknown"] = destination };
            }

            List<Dictionary<string, object>> Conditions(List<ConditionConstant> conditions) =>
                conditions.Select(c => new Dictionary<string, object>
                {
                    ["param"] = Name(c.m_EventID),
                    ["mode"] = Mode(c.m_ConditionMode),
                    ["value"] = c.m_EventThreshold,
                }).ToList();

            // sourceFrames is null for AnyState, whose source is whatever state is playing.
            Dictionary<string, object> Transition(TransitionConstant t, int? sourceFrames)
            {
                var entry = new Dictionary<string, object>
                {
                    ["to"] = Target(t.m_DestinationState),
                    ["conditions"] = Conditions(t.m_ConditionConstantArray),
                };
                if (t.m_HasExitTime)
                {
                    if (t.m_UseFrameCount)
                        entry["exitFrame"] = t.m_FrameCount;
                    else if (sourceFrames != null)
                        entry["exitFrame"] = (int)Math.Round(t.m_ExitTime * sourceFrames.Value);
                    else
                        entry["exitNormalized"] = t.m_ExitTime;
                }
                if (t.m_HasFixedDuration)
                    entry["blendFrames"] = (int)Math.Round(t.m_TransitionDuration * rate);
                else if (sourceFrames != null)
                    entry["blendFrames"] = (int)Math.Round(t.m_TransitionDuration * sourceFrames.Value);
                else
                    entry["blendNormalized"] = t.m_TransitionDuration;
                if (t.m_UseFrameCount)
                    entry["offsetFrames"] = t.m_TransitionOffsetCount;
                else
                    entry["offsetNormalized"] = t.m_TransitionOffset;
                entry["canTransitionToSelf"] = t.m_CanTransitionToSelf;
                return entry;
            }

            for (int i = 0; i < states.Count; i++)
            {
                states[i]["transitions"] = machine.m_StateConstantArray[i].m_TransitionConstantArray
                    .Select(t => Transition(t, stateFrames[i])).ToList();
            }

            var parameters = new List<Dictionary<string, object>>();
            var defaults = cc.m_DefaultValues;
            foreach (var value in cc.m_Values?.m_ValueArray ?? new List<ValueConstant>())
            {
                var type = ParameterType(value.m_Type);
                object initial = type switch
                {
                    "Float" => At(defaults?.m_FloatValues, value.m_Index, 0f),
                    "Int" => At(defaults?.m_IntValues, value.m_Index, 0),
                    "Bool" or "Trigger" => At(defaults?.m_BoolValues, value.m_Index, false),
                    _ => null,
                };
                parameters.Add(new Dictionary<string, object>
                {
                    ["name"] = Name(value.m_ID),
                    ["type"] = type,
                    ["default"] = initial,
                });
            }

            return new Dictionary<string, object>
            {
                ["format"] = "AnimeStudio animator graph 1",
                ["controller"] = controller.m_Name,
                ["frameRate"] = rate,
                ["parameters"] = parameters,
                ["layer"] = new Dictionary<string, object>
                {
                    ["name"] = Name(layer.m_Binding),
                    ["defaultState"] = (int)machine.m_DefaultState,
                    ["states"] = states,
                    ["anyState"] = machine.m_AnyStateTransitionConstantArray.Select(t => Transition(t, null)).ToList(),
                    ["selectors"] = (machine.m_SelectorStateConstantArray ?? new List<SelectorStateConstant>())
                        .Select(s => new Dictionary<string, object>
                        {
                            ["entry"] = s.m_isEntry,
                            ["transitions"] = s.m_TransitionConstantArray.Select(st => new Dictionary<string, object>
                            {
                                ["to"] = Target(st.m_Destination),
                                ["conditions"] = Conditions(st.m_ConditionConstantArray),
                            }).ToList(),
                        }).ToList(),
                },
            };
        }

        private static T At<T>(T[] values, uint index, T fallback) =>
            values != null && index < values.Length ? values[index] : fallback;

        // Unity's condition modes, plus 9, which ZZZ uses as the lower bound of a frame window.
        private static string Mode(uint mode) => mode switch
        {
            1 => "If",
            2 => "IfNot",
            3 => "Greater",
            4 => "Less",
            5 => "ExitTime",
            6 => "Equals",
            7 => "NotEqual",
            9 => "GreaterEqual",
            _ => "mode" + mode,
        };

        private static string ParameterType(uint type) => type switch
        {
            1 => "Float",
            3 => "Int",
            4 => "Bool",
            9 => "Trigger",
            _ => "type" + type,
        };
    }
}
