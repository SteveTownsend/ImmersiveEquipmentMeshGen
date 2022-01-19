using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Skyrim;
using System.Threading.Tasks;
using SSEForms = Mutagen.Bethesda.FormKeys.SkyrimSE;
using nifly;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Archives;

namespace ImmersiveEquipmentDisplay
{
    public class MeshHandler
    {
        private class TargetMeshInfo
        {
            public readonly string originalName;
            public readonly ModelType modelType;
            public TargetMeshInfo(string name, ModelType model)
            {
                originalName = name;
                modelType = model;
            }
        }
        public Settings _settings { get; }
        public static readonly string MeshPrefix = "meshes\\";

        private IDictionary<string, TargetMeshInfo> targetMeshes = new Dictionary<string, TargetMeshInfo>();
        private readonly object recordLock = new object();

        public enum ModelType {
            Unknown = 0,
            Sword,
            Dagger,
            Mace,
            Axe,
            Staff,
            TwoHandMelee,
            TwoHandRange,
            Shield
        };

        internal enum WeaponType {
            Unknown = 0,
            OneHandMelee,
            TwoHandMelee,
            Shield,
            TwoHandRange,
            Staff
        };

        private static readonly IDictionary<ModelType, WeaponType> weaponTypeByModelType = new Dictionary<ModelType, WeaponType>
        {
            { ModelType.Unknown, WeaponType.Unknown },
            { ModelType.Sword, WeaponType.OneHandMelee },
            { ModelType.Dagger, WeaponType.OneHandMelee },
            { ModelType.Mace, WeaponType.OneHandMelee },
            { ModelType.Axe, WeaponType.OneHandMelee },
            { ModelType.Staff, WeaponType.Staff },
            { ModelType.TwoHandMelee, WeaponType.TwoHandMelee },
            { ModelType.TwoHandRange, WeaponType.TwoHandRange },
            { ModelType.Shield, WeaponType.Shield }
        };

        private int countSkipped;
        private int countCandidates;
        internal int countGenerated;
        private int countFailed;

        public MeshHandler(Settings settings)
        {
            _settings = settings;
        }

        private bool AddMesh(string modelPath, ModelType modelType)
        {
            if (!_settings.meshes.IsNifValid(modelPath))
            {
                _settings.diagnostics.logger.WriteLine("Filters skip {0}", modelPath);
                ++countSkipped;
                return false;
            }
            // Do not add the same model more than once - model reuse is common
            string normalizedPath = modelPath.ToLower();
            if (targetMeshes.ContainsKey(normalizedPath))
            {
                return false;
            }
            targetMeshes.Add(normalizedPath, new TargetMeshInfo(modelPath, modelType));
            return true;
        }

        private void RecordModel(FormKey record, ModelType modelType, IModelGetter model)
        {
            string modelPath = model.File;
            if (AddMesh(modelPath, modelType))
            {
                _settings.diagnostics.logger.WriteLine("Model {0}/{1} with type {2} added", record, modelPath, modelType.ToString());
            }
        }

        private void CollateWeapons()
        {
            foreach (var weap in ScriptLess.PatcherState.LoadOrder.PriorityOrder.WinningOverrides<IWeaponGetter>())
            {
                // skip if no model
                if (weap == null || weap.Model == null)
                    continue;
                // skip non-playable
                if (weap.MajorFlags.HasFlag(Weapon.MajorFlag.NonPlayable))
                    continue;
                // skip scan if no Keywords
                ModelType modelType = ModelType.Unknown;
                if (weap.Keywords != null)
                {
                    foreach (var keyword in weap.Keywords)
                    {
                        if (keyword.FormKey == SSEForms.Skyrim.Keyword.WeapTypeDagger.FormKey)
                        {
                            modelType = ModelType.Dagger;
                            break;
                        }
                        if (keyword.FormKey == SSEForms.Skyrim.Keyword.WeapTypeSword.FormKey)
                        {
                            modelType = ModelType.Sword;
                            break;
                        }
                        if (keyword.FormKey == SSEForms.Skyrim.Keyword.WeapTypeWarAxe.FormKey)
                        {
                            modelType = ModelType.Axe;
                            break;
                        }
                        if (keyword.FormKey == SSEForms.Skyrim.Keyword.WeapTypeMace.FormKey)
                        {
                            modelType = ModelType.Mace;
                            break;
                        }
                        if (keyword.FormKey == SSEForms.Skyrim.Keyword.WeapTypeBattleaxe.FormKey ||
                            keyword.FormKey == SSEForms.Skyrim.Keyword.WeapTypeGreatsword.FormKey ||
                            keyword.FormKey == SSEForms.Skyrim.Keyword.WeapTypeWarhammer.FormKey)
                        {
                            modelType = ModelType.TwoHandMelee;
                            break;
                        }
                        if (keyword.FormKey == SSEForms.Skyrim.Keyword.WeapTypeBow.FormKey)
                        {
                            modelType = ModelType.TwoHandRange;
                            break;
                        }
                        if (keyword.FormKey == SSEForms.Skyrim.Keyword.WeapTypeStaff.FormKey)
                        {
                            modelType = ModelType.Staff;
                            break;
                        }
                    }
                }

                // check animation if weapon type not determined yet
                if (modelType == ModelType.Unknown && weap.Data != null)
                {
                    //currently required for: SSM Spears
                    if (weap.Data.AnimationType == WeaponAnimationType.OneHandSword)
                    {
                        modelType = ModelType.Sword;
                    }
                }

                // skip records with indeterminate weapon type
                if (modelType == ModelType.Unknown)
                    continue;

                RecordModel(weap.FormKey, modelType, weap.Model);

                // check first-person model in WNAM, that must be transformed too
                IStaticGetter? staticGetter = weap.FirstPersonModel.TryResolve(ScriptLess.PatcherState.LinkCache);
                if (staticGetter is not null && staticGetter.Model is not null)
                {
                    RecordModel(staticGetter.FormKey, modelType, staticGetter.Model);
                }
            }
        }

        private ModelType FinalizeModelType(NifFile nif, string modelPath, ModelType modelType)
        {
            bool rightHanded = modelPath.Contains("Right.nif", StringComparison.OrdinalIgnoreCase);
            using var header = nif.GetHeader();
            NiNode? node = header.GetBlockById(0) as NiNode;
            if (node == null)
            {
                _settings.diagnostics.logger.WriteLine("Expected NiNode at offset 0 not found");
                return ModelType.Unknown;
            }
            // analyze 'Prn' in ExtraData for first block
            using var children = nif.StringExtraDataChildren(node, true);
            foreach (NiStringExtraData extraData in children)
            {
                using (extraData)
                {
                    var refs = extraData.GetStringRefList();
                    if (refs.Count != 2)
                        continue;
                    using NiStringRef refKey = refs[0];
                    if (refKey.get() == "Prn")
                    {
                        using NiStringRef refValue = refs[1];
                        string tag = refValue.get();
                        if (modelType == ModelType.Unknown)
                        {
                            if (tag == "WeaponDagger")
                            {
                                modelType = ModelType.Dagger;
                            }
                            else if (tag == "WeaponSword")
                            {
                                modelType = ModelType.Sword;
                            }
                            else if (tag == "WeaponAxe")
                            {
                                modelType = ModelType.Axe;
                            }
                            else if (tag == "WeaponMace")
                            {
                                modelType = ModelType.Mace;
                            }
                            else if (tag == "WeaponStaff" && !rightHanded)
                            {
                                //Filter out meshes using DSR file naming convention.
                                //Vanilla staves may have incorrect Prn, USP fixed Staff01
                                modelType = ModelType.Staff;
                            }
                            else if (tag == "WeaponBack")
                            {
                                modelType = ModelType.TwoHandMelee;
                            }
                            else if (tag == "WeaponBow")
                            {
                                modelType = ModelType.TwoHandRange;
                            }
                            else if (tag == "SHIELD")
                            {
                                modelType = ModelType.Shield;
                            }
                        }
                        else if (modelType == ModelType.Staff)
                        // Sword of amazement brought this up. Staves can't share with OneHand meshes since they both use '*Left.nif'
                        // So One Hand Weapon Node in the Prn overrides Keyword:WeaponTypeStaff
                        {
                            if (tag == "WeaponDagger")
                            {
                                modelType = ModelType.Dagger;
                            }
                            else if (tag == "WeaponSword")
                            {
                                modelType = ModelType.Sword;
                            }
                            else if (tag == "WeaponAxe")
                            {
                                modelType = ModelType.Axe;
                            }
                            else if (tag == "WeaponMace")
                            {
                                modelType = ModelType.Mace;
                            }
                        }
                        break;
                    }
                }
            }
            return modelType;
        }

        public void GenerateMesh(NifFile nif, string originalPath, string newPath, ModelType modelType)
        {
            try
            {
                modelType = FinalizeModelType(nif, originalPath, modelType);

                WeaponType weaponType = weaponTypeByModelType[modelType];
                if (weaponType != WeaponType.OneHandMelee)
                {
                    _settings.diagnostics.logger.WriteLine("Skip {0}, incorrect WeaponType {1}", originalPath, weaponType);
                    Interlocked.Increment(ref countSkipped);
                }
                else
                {
                    // TODO selective patching by weapon type would need a filter here
                    Interlocked.Increment(ref countCandidates);
                    using NifTransformer transformer = new NifTransformer(this, nif, originalPath, newPath, modelType, weaponType);
                    transformer.Generate();
                }
            }
            catch (Exception e)
            {
                Interlocked.Increment(ref countFailed);
                _settings.diagnostics.logger.WriteLine("Exception processing {0}: {1}", originalPath, e.GetBaseException());
            }
        }

        // Mesh Generation logic originally from 'AllGUD Weapon Mesh Generator.pas'
        internal void TransformMeshes()
        {
            // no op if empty
            if (targetMeshes.Count == 0)
            {
                _settings.diagnostics.logger.WriteLine("No meshes require transformation");
                return;
            }
            IDictionary<string, string> bsaFiles = new ConcurrentDictionary<string, string>();
            int totalMeshes = targetMeshes.Count;

            IDictionary<string, byte> looseDone = new ConcurrentDictionary<string, byte>();
            Parallel.ForEach(targetMeshes, kv =>
            {
                // loose file wins over BSA contents
                string originalFile = _settings.meshes.InputFolder + MeshPrefix + kv.Key;
                string newFile = _settings.meshes.OutputFolder + MeshPrefix + kv.Key;
                if (File.Exists(originalFile))
                {
                    _settings.diagnostics.logger.WriteLine("Transform mesh from loose file {0}", originalFile);

                    using NifFile nif = new NifFile();
                    nif.Load(originalFile);
                    GenerateMesh(nif, originalFile, newFile, kv.Value.modelType);
                    looseDone.Add(kv.Key, 0);
                }
                else
                {
                    // check for this file in archives
                    bsaFiles.Add(MeshPrefix + kv.Key, kv.Key);
                }
            });

            IDictionary<string, string> bsaDone = new ConcurrentDictionary<string, string>();
            if (bsaFiles.Count > 0)
            {
                // Introspect all known BSAs to locate meshes not found as loose files. Dups are ignored - first find wins.
                foreach (var bsaFile in Archive.GetApplicableArchivePaths(GameRelease.SkyrimSE, ScriptLess.PatcherState.DataFolderPath))
                {
                    var bsaReader = Archive.CreateReader(GameRelease.SkyrimSE, bsaFile);
                    bsaReader.Files.AsParallel().
                        Where(candidate => bsaFiles.ContainsKey(candidate.Path.ToLower())).
                        ForAll(bsaMesh =>
                    {
                        try
                        {
                            string rawPath = bsaFiles[bsaMesh.Path.ToLower()];
                            TargetMeshInfo meshInfo = targetMeshes[rawPath];
                            if (bsaDone.ContainsKey(rawPath))
                            {
                                _settings.diagnostics.logger.WriteLine("Mesh {0} from BSA {1} already processed from BSA {2}", bsaMesh.Path, bsaFile, bsaDone[rawPath]);
                                return;
                            }

                            // Load NIF from stream via String - must rewind first
                            byte[] bsaData = bsaMesh.GetBytes();
                            using vectoruchar bsaBytes = new vectoruchar(bsaData);

                            using (var nif = new NifFile(bsaBytes))
                            {
                                _settings.diagnostics.logger.WriteLine("Transform mesh {0} from BSA {1}", bsaMesh.Path, bsaFile);
                                string newFile = _settings.meshes.OutputFolder + bsaMesh.Path;
                                GenerateMesh(nif, bsaMesh.Path, newFile, targetMeshes[rawPath].modelType);
                            }
                            bsaDone.Add(rawPath, bsaFile);
                        }
                        catch (Exception e)
                        {
                            _settings.diagnostics.logger.WriteLine("Exception on mesh {0} from BSA {1}: {2}", bsaMesh.Path, bsaFile, e.GetBaseException());
                        }
                    });
                }
            }

            var missingFiles = targetMeshes.Where(kv => !looseDone.ContainsKey(kv.Key) && !bsaDone.ContainsKey(kv.Key)).ToList();
            foreach (var mesh in missingFiles)
            {
                _settings.diagnostics.logger.WriteLine("Referenced Mesh {0} not found loose or in BSA", mesh.Key);
            }
            _settings.diagnostics.logger.WriteLine("{0} total meshes: found {1} Loose, {2} in BSA, {3} missing files",
                targetMeshes.Count, looseDone.Count, bsaDone.Count, missingFiles.Count);
            _settings.diagnostics.logger.WriteLine("Generated {0}, Candidates {1}, Skipped {2}, Failed {3}",
                countGenerated, countCandidates, countSkipped, countFailed);
        }

        internal void Analyze()
        {
            // inventory the meshes to be transformed
            CollateWeapons();
        }
    }
}
