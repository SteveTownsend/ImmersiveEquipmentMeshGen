using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Collections.Generic;
using nifly;
using ModelType = ImmersiveEquipmentDisplay.MeshHandler.ModelType;
using WeaponType = ImmersiveEquipmentDisplay.MeshHandler.WeaponType;

namespace ImmersiveEquipmentDisplay
{
    class NifTransformer : IDisposable
    {
        private static readonly string ScbTag = "Scb";
        private static readonly string LeftSuffix = "Left";

        MeshHandler meshHandler;
        NifFile nif;
        niflycpp.BlockCache blockCache;
        NiHeader header;
        WeaponType nifWeapon;
        ModelType nifModel;
        string nifPath;

        bool meshHasController;

        ISet<uint> rootChildIds = new SortedSet<uint>();

        internal NifTransformer(MeshHandler handler, NifFile source, string modelPath, ModelType modelType, WeaponType weaponType)
        {
            meshHandler = handler;
            nif = source;
            blockCache = new niflycpp.BlockCache(nif.GetHeader());
            header = blockCache.Header;
            nifPath = modelPath;
            nifModel = modelType;
            nifWeapon = weaponType;
        }

        public void Dispose()
        {
            blockCache.Dispose();
        }

        private void UnskinShader(NiBlockRefNiShader shaderRef)
        {
            NiShader shader = blockCache.EditableBlockById<NiShader>(shaderRef.index);
            if (shader == null)
            {
                meshHandler._settings.diagnostics.logger.WriteLine("Expected NiShader at offset {0} not found", shaderRef.index);
                return;
            }
            shader.SetSkinned(false);
        }

        private void ApplyTransformToChild(NiAVObject parent, NiAVObject child, uint childId, bool isBow)
        {
            using MatTransform cTransform = child.transform;
            using MatTransform pTransform = parent.transform;
            using MatTransform transform = pTransform.ComposeTransforms(cTransform);
            child.transform = transform;

            // Don't do this for shapes, Don't remove Transforms of Shapes in case they need to be mirrored
            if (child.controllerRef != null && !child.controllerRef.IsEmpty())
            {
                NiTransformController controller = blockCache.EditableBlockById<NiTransformController>(child.controllerRef.index);
                if (controller != null)
                {
                    meshHasController = true;
                    // TODO requires enhancement for dynamic display
                    //				if not bUseTemplates then
                    //					exit;
                }
                if (!meshHasController)
                {
                    meshHandler._settings.diagnostics.logger.WriteLine("Expected NiTransformController at offset {0} not found", child.controllerRef.index);
                }
            }
            TransformChildren(child, childId, isBow);
        }

        private void TransformChildren(NiAVObject blockObj, uint blockId, bool isBow)
        {
            ISet<uint> childDone = new HashSet<uint>();
            using var childNodes = blockObj.CopyChildRefs();
            foreach (var childNode in childNodes)
            {
                using (childNode)
                {
                    if (childDone.Contains(childNode.index))
                        continue;
                    childDone.Add(childNode.index);
                    var subBlock = blockCache.EditableBlockById<NiAVObject>(childNode.index);
                    if (subBlock == null)
                        continue;
                    meshHandler._settings.diagnostics.logger.WriteLine("Applying Transform of Block:{0} to its Child:{1}", blockId, childNode.index);
                    ApplyTransformToChild(blockObj, subBlock, childNode.index, isBow);
                }
                if (!isBow)
                {
                    using MatTransform transform = new MatTransform();
                    using Matrix3 rotation = Matrix3.MakeRotation(0.0f, 0.0f, 0.0f);  // yaw, pitch, roll
                    transform.rotation = rotation;
                    using Vector3 translation = new Vector3();
                    translation.Zero();
                    transform.translation = translation;
                    transform.scale = 1.0f;
                    blockObj.transform = transform;
                }
            }
        }

        private void TransferVertexData(NiSkinPartition skinPartition, BSTriShape? bsTriShape)
        {
            if (bsTriShape == null)
                return;
            // Copy Vertex Data from NiSkinPartition
            using var vertData = skinPartition.vertData;
            bsTriShape.SetVertexData(vertData);

            // Get the first partition, where Triangles and the rest is stored
            // Haven't seen any with multiple partitions.
            // Not sure how that would work, revisit if there's a problem.
            using var partitions = skinPartition.partitions;
            using var thePartition = partitions[0];
            using var triangles = thePartition.triangles;
            bsTriShape.SetTriangles(triangles);

            bsTriShape.UpdateBounds();
        }

        private void TransformScale(NiShape parent, NiSkinInstance skinInstance)
        {
            // On the first bone, hope all bones have the same scale here! cause seriously, what the heck could you do if they weren't?
            using var skinBoneRefs = skinInstance.boneRefs;
            using var boneRefs = skinBoneRefs.GetRefs();
            foreach (var boneRef in boneRefs)
            {
                using (boneRef)
                {
                    var bone = blockCache.EditableBlockById<NiNode>(boneRef.index);
                    if (bone != null)
                    {
                        MatTransform rootTransform = parent.transform;
                        rootTransform.scale *= bone.transform.scale;
                        parent.transform = rootTransform;
                        break;
                    }
                }
            }

            using var dataRef = skinInstance.dataRef;
            if (!dataRef.IsEmpty())
            {
                NiSkinData skinData = blockCache.EditableBlockById<NiSkinData>(dataRef.index);
                if (skinData != null)
                {
                    using MatTransform rootTransform = parent.transform;
                    rootTransform.scale *= skinData.skinTransform.scale;
                    parent.transform = rootTransform;
                    if (skinData.bones.Count > 0)
                    {
                        rootTransform.scale *= skinData.bones[0].boneTransform.scale;
                    }
                }
                else
                {
                    meshHandler._settings.diagnostics.logger.WriteLine("Expected NiSkinData at offset {0} not found", skinInstance.dataRef.index);
                }
            }
        }

        private bool RemoveSkin(ISet<uint> skinDone, NiAVObject blockObj)
        {
            if (!(blockObj is BSTriShape) && !(blockObj is NiTriShape) && !(blockObj is NiTriStrips))
            {
                // Non-trishape, FIND THE CHILDREN AND REMOVE THEIR SKIN!
                using var childNodes = blockObj.CopyChildRefs();
                foreach (var childNode in childNodes)
                {
                    using (childNode)
                    {
                        if (skinDone.Contains(childNode.index))
                            continue;
                        skinDone.Add(childNode.index);
                        var block = blockCache.EditableBlockById<NiAVObject>(childNode.index);
                        if (block == null)
                            continue;
                        meshHandler._settings.diagnostics.logger.WriteLine("Removing Skin @ Block: {0}", childNode.index);
                        RemoveSkin(skinDone, block);
                    }
                }
                return false;
            }

            // Basically just remove anything related to skin
            NiShape? niShape = blockObj as NiShape;
            if (niShape != null)
            {
                // Remove skin flag from shader		
                if (niShape.HasShaderProperty())
                {
                    // remove unnecessary skinning on bows.
                    using var shaderRef = niShape.ShaderPropertyRef();
                    UnskinShader(shaderRef);
                }
                // Remove skin from BSTriShape
                if (niShape.HasSkinInstance())
                {
                    niShape.SetSkinned(false);
                    using var skinRef = niShape.SkinInstanceRef();
                    NiSkinInstance skinInstance = blockCache.EditableBlockById<NiSkinInstance>(skinRef.index);
                    if (skinInstance != null)
                    {
                        using var partitionRef = skinInstance.skinPartitionRef;
                        if (!partitionRef.IsEmpty())
                        {
                            NiSkinPartition skinPartition = blockCache.EditableBlockById<NiSkinPartition>(partitionRef.index);
                            if (skinPartition != null)
                            {
                                TransferVertexData(skinPartition, niShape as BSTriShape);
                            }
                        }
                        else
                        {
                            meshHandler._settings.diagnostics.logger.WriteLine("Expected NiSkinPartition at offset {0} not found", partitionRef.index);
                        }

                        // Check for all scale transforms.
                        TransformScale(niShape, skinInstance);
                        // Remove the entire SkinInstance from the dest NIF
                        niShape.SetSkinInstanceRef(niflycpp.NIF_NPOS);
                    }
                    else
                    {
                        meshHandler._settings.diagnostics.logger.WriteLine("Expected NiSkinInstance at offset {0} not found", skinRef.index);
                    }

                }
            }
            return true;
        }

        // Rename scabbard and any children, we are copying the entire tree mirrored into the dest mesh
        private void AddScabbardMirror(ISet<uint> alreadyDone, NiAVObject source, NiNode parent)
        {
            using NiAVObject blockDest = niflycpp.BlockCache.SafeClone<NiAVObject>(source);

            using var blockName = blockDest.name;
            string newName = blockName.get() + LeftSuffix;
            uint newId = header.AddOrFindStringId(newName);
            NiStringRef newRef = new NiStringRef(newName);
            newRef.SetIndex(newId);
            blockDest.name = newRef;
            // Hide the mirror, Immersive Equipment Display/Simple Dual Sheath will unhide as needed
            if (blockName.get().Equals(ScbTag))
            {
                blockDest.flags = blockDest.flags | 0x1;
            }

            if (blockDest is BSTriShape || blockDest is NiTriShape || blockDest is NiTriStrips)
            {
                if (IsBloodMesh(blockDest as NiShape))
                    return;

                // TODO it appears these functions could be combined. Stick with script flow for safety, at least initially.
                ApplyTransform(newId, blockDest); //In case things are at an angle where flipping x would produce incorrect results.
                FlipAlongZ(newId, blockDest);
            }
            else
            {
                NiNode? destNode = blockDest as NiNode;
                if (destNode is not null)
                {
                    destNode.childRefs = new NiBlockRefArrayNiAVObject();
                    // Copy Blocks all the way down until a trishape is reached
                    using var childNodes = destNode.CopyChildRefs();
                    foreach (var childNode in childNodes)
                    {
                        using (childNode)
                        {
                            var block = blockCache.EditableBlockById<NiAVObject>(childNode.index);
                            if (block == null)
                                continue;
                            if (alreadyDone.Contains(childNode.index))
                                continue;
                            alreadyDone.Add(childNode.index);
                            meshHandler._settings.diagnostics.logger.WriteLine("AddScabbardMirror checking Child {0}", childNode.index);
                            AddScabbardMirror(alreadyDone, block, destNode);
                        }
                    }
                }
            }

            // Insert new block in the mesh once all editing due to child content is complete
            header.AddBlock(blockDest);
            nif.SetParentNode(blockDest, parent);
        }

        private bool IsBloodMesh(NiShape? shape)
        {
            // Check if the Shape is a bloodmesh. Shapes can be treated polymorphically.
            // Blood meshes don't get used for the armor and just take up space.
            // Let's just scan the textures for 'BloodEdge'??? That's like the only commonality I can find.
            // Especially since there's a mod with a SE Mesh that has improper data that makes it cause CTD and this is the only thing I can use to catch it.
            if (shape == null || !shape.HasShaderProperty())
                return false;
            using NiBlockRefNiShader shaderPropertyRef = shape.ShaderPropertyRef();
            if (shaderPropertyRef == null || shaderPropertyRef.IsEmpty())
                return false;
            BSShaderProperty shaderProperty = blockCache.EditableBlockById<BSShaderProperty>(shaderPropertyRef.index);
            if (shaderProperty != null)
            {
                if (shaderProperty.HasWeaponBlood())
                    return true;
                if (shaderProperty.HasTextureSet())
                {
                    using var textureSetRef = shaderProperty.TextureSetRef();
                    if (!textureSetRef.IsEmpty())
                    {
                        BSShaderTextureSet textureSet = blockCache.EditableBlockById<BSShaderTextureSet>(textureSetRef.index);
                        if (textureSet != null)
                        {
                            using var textures = textureSet.textures;
                            using var texturePaths = textures.items();
                            using var firstPath = texturePaths[0];
                            string texturePath = firstPath.get();
                            // Skullcrusher users bloodhit
                            if (texturePath.Contains("blood\\bloodedge", StringComparison.OrdinalIgnoreCase) ||
                                texturePath.Contains("blood\\bloodhit", StringComparison.OrdinalIgnoreCase))
                                return true;
                        }
                        else
                        {
                            meshHandler._settings.diagnostics.logger.WriteLine("Expected BSShaderTextureSet at offset {0} not found", textureSetRef.index);
                        }
                    }
                }

                // NiTriShape blood has a NiStringExtraData sub-block named 'Keep' and 'NiHide' as its data.
                // This was the original, dunno if Kesta needed it for something specific or not?
                // Saw some meshes that couldn't keep this straight, and had NiHide/Keep reversed.
                using var extraDataRefs = shape.extraDataRefs;
                using var refList = extraDataRefs.GetRefs();
                foreach (NiBlockRefNiExtraData extraDataRef in refList)
                {
                    using (extraDataRef)
                    {
                        if (extraDataRef.IsEmpty())
                            continue;
                        NiStringExtraData stringExtraData = blockCache.EditableBlockById<NiStringExtraData>(extraDataRef.index);
                        if (stringExtraData != null)
                        {
                            using var name = stringExtraData.name;
                            using var stringData = stringExtraData.stringData;
                            if (name.get() == "Keep" && stringData.get() == "NiHide")
                                return true;
                        }
                        else
                        {
                            meshHandler._settings.diagnostics.logger.WriteLine("Expected NiStringExtraData at offset {0} not found", extraDataRef.index);
                        }
                    }
                }
            }
            else
            {
                meshHandler._settings.diagnostics.logger.WriteLine("Expected BSShaderProperty at offset {0} not found", shaderPropertyRef.index);
            }
            return false;
        }
		
		// Avoid Access Violation in C++ code
        private void CheckSetNormals(uint id, BSTriShape shape, vectorVector3 rawNormals, int vertexCount)
        {
            if (shape.GetNumVertices() != rawNormals.Count)
            {
                throw new InvalidOperationException(String.Format("Shape @ {0} in {1} has NumVertices {2}: trying to update with {3} raw Normals",
                    id, nifPath, shape.GetNumVertices(), rawNormals.Count));
            }
            if (shape.GetNumVertices() != vertexCount)
            {
                throw new InvalidOperationException(String.Format("Shape @ {0} in {1} has NumVertices {2}: trying to update with {3} VertexData",
                    id, nifPath, shape.GetNumVertices(), rawNormals.Count));
            }
            shape.SetNormals(rawNormals);
        }

        private void FlipAlongZ(uint id, NiAVObject block)
        {
            if (block is BSTriShape)
            {
                BSTriShape? bsTriShape = block as BSTriShape;
                if (bsTriShape == null)
                    return;
                try
                {
                    using vectorBSVertexData vertexDataList = new vectorBSVertexData();
                    using var vertData = bsTriShape.vertData;
                    using var rawNormals = bsTriShape.UpdateRawNormals();
                    using var newRawNormals = new vectorVector3();
                    foreach (var vertexNormal in vertData.Zip(rawNormals, Tuple.Create))
                    {
                        using BSVertexData vertexData = vertexNormal.Item1;
                        using Vector3 rawNormal = vertexNormal.Item2;
                        using Vector3 newVertex = new Vector3(vertexData.vert);
                        newVertex.z = -newVertex.z;

                        using BSVertexData newVertexData = new BSVertexData(vertexData);
                        newVertexData.vert = newVertex;
                        vertexDataList.Add(newVertexData);

                        rawNormal.z = -rawNormal.z;
                        newRawNormals.Add(rawNormal);
                    }
                    bsTriShape.vertData = vertexDataList;
                    CheckSetNormals(id, bsTriShape, newRawNormals, vertexDataList.Count);

                    using  vectorTriangle newTriangles = new vectorTriangle();
                    using var oldTriangles = bsTriShape.triangles;
                    foreach (Triangle triangle in oldTriangles)
                    {
                        using (triangle)
                        {
                            using Triangle newTriangle = new Triangle(triangle.p3, triangle.p2, triangle.p1);
                            newTriangles.Add(newTriangle);
                        }
                    }
                    bsTriShape.triangles = newTriangles;
                }
                catch (Exception e)
                {
                    meshHandler._settings.diagnostics.logger.WriteLine("Exception for Block Data in BSTriShape {0} in {1} : {2}", id, nifPath, e.GetBaseException());
                }
                bsTriShape.UpdateBounds();
                try // Non-vital
                {
                    // TODO is this the right mapping?
                    // aTriShape.UpdateTangents;
                    bsTriShape.CalcTangentSpace();
                }
                catch (Exception e)
                {
                    meshHandler._settings.diagnostics.logger.WriteLine("Exception updating Tangents in left-hand variant(s) for: {0} in {1} : {2}",
                        id, nifPath, e.GetBaseException());
                }
            }
            else if (block is NiTriStrips || block is NiTriShape)
            {
                NiGeometry? niGeometry = block as NiGeometry;
                if (niGeometry == null)
                    return;
                using var dataRef = niGeometry.DataRef();
                if (!dataRef.IsEmpty())
                {
                    NiGeometryData geometryData = blockCache.EditableBlockById<NiGeometryData>(dataRef.index);
                    if (geometryData != null)
                    {
                        using vectorVector3 newVertices = new vectorVector3();
                        using var vertices = geometryData.vertices;
                        foreach (Vector3 vertex in vertices)
                        {
                            using Vector3 newVertex = new Vector3(vertex);
                            newVertex.z = -newVertex.z;
                            newVertices.Add(newVertex);
                        }
                        geometryData.vertices = newVertices;

                        using vectorVector3 normals = geometryData.normals;
                        if (normals != null)
                        {
                            using vectorVector3 newNormals = new vectorVector3();
                            using var dataNormals = geometryData.normals;
                            foreach (Vector3 normal in dataNormals)
                            {
                                using Vector3 newNormal = new Vector3(normal);
                                newNormal.z = -newNormal.z;
                                newNormals.Add(newNormal);
                            }
                            geometryData.normals = newNormals;

                            using vectorTriangle newTriangles = new vectorTriangle();
                            using var triangles = geometryData.Triangles();
                            foreach (Triangle triangle in triangles)
                            {
                                using (triangle)
                                {
                                    newTriangles.Add(new Triangle(triangle.p3, triangle.p2, triangle.p1));
                                }
                            }
                            geometryData.SetTriangles(newTriangles);
                        }
                        geometryData.UpdateBounds();
                        try // Non-vital
                        {
                            // TODO is this the right mapping?
                            geometryData.CalcTangentSpace();
                            // TriShapeData.UpdateTangents;
                        }
                        catch (Exception e)
                        {
                            meshHandler._settings.diagnostics.logger.WriteLine("Exception when updating the Tangent for the left-hand variant(s) for NiGeometry {0} in {1} : {2}",
                                id, nifPath, e.GetBaseException());
                        }
                    }
                }
            }
        }

        private void ApplyTransform(uint id, NiAVObject block)
        {
            using MatTransform transform = block.transform;
            float scale = transform.scale;
            using Vector3 translation = transform.translation;
            using Matrix3 rotation = transform.rotation;
            rotation.SetPrecision(4);

            // Check if anything is transformed
            if (scale == 1 && translation.IsZero() && rotation.IsIdentity())
                return;

            if (block is BSTriShape)
            {
                BSTriShape? bsTriShape = block as BSTriShape;
                if (bsTriShape == null)
                    return;
                try
                {
                    using vectorBSVertexData vertexDataList = new vectorBSVertexData();
                    using var vertData = bsTriShape.vertData;
                    using var rawNormals = bsTriShape.UpdateRawNormals();
                    using var newRawNormals = new vectorVector3();
                    foreach (var vertexNormal in vertData.Zip(rawNormals, Tuple.Create))
                    {
                        using BSVertexData vertexData = vertexNormal.Item1;
                        using Vector3 rawNormal = vertexNormal.Item2;
                        using var vert = vertexData.vert;
                        using var rMultV = rotation.opMult(vert);
                        using var rMultVMultS = rMultV.opMult(scale);
                        using Vector3 newVertex = rMultVMultS.opAdd(translation);
                        using BSVertexData newVertexData = new BSVertexData(vertexData);
                        newVertexData.vert = newVertex;
                        vertexDataList.Add(newVertexData);

                        using Vector3 newRawNormal = rotation.opMult(rawNormal);
                        newRawNormals.Add(newRawNormal);
                    }
                    bsTriShape.vertData = vertexDataList;
                    CheckSetNormals(id, bsTriShape, newRawNormals, vertexDataList.Count);
                }
                catch (Exception e)
                {
                    meshHandler._settings.diagnostics.logger.WriteLine("Exception for Block Data for BSTriShape {0} in {1} : {2}", id, nifPath, e.GetBaseException());
                }
                bsTriShape.UpdateBounds();
                try // Non-vital
                {
                    // TODO is this the right mapping?
                    // aTriShape.UpdateTangents;
                    bsTriShape.CalcTangentSpace();
                }
                catch (Exception e)
                {
                    meshHandler._settings.diagnostics.logger.WriteLine("Exception when updating the Tangent for the left-hand variant(s) for: {0} in {1} : {2}",
                        id, nifPath, e.GetBaseException());
                }
            }
            else
            {
                if (block is NiTriStrips || block is NiTriShape)
                {
                    NiGeometry? niGeometry = block as NiGeometry;
                    if (niGeometry == null)
                        return;
                    using var dataRef = niGeometry.DataRef();
                    if (!dataRef.IsEmpty())
                    {
                        NiGeometryData geometryData = blockCache.EditableBlockById<NiGeometryData>(dataRef.index);
                        if (geometryData != null)
                        {
                            using vectorVector3 newVertices = new vectorVector3();
                            using var vertices = geometryData.vertices;
                            foreach (Vector3 vertex in vertices)
                            {
                                using (vertex)
                                {
                                    using var rMultV = rotation.opMult(vertex);
                                    using var rMultVMultS = rMultV.opMult(scale);
                                    using Vector3 newVertex = rMultVMultS.opAdd(translation);
                                    newVertices.Add(newVertex);

                                }
                            }
                            geometryData.vertices = newVertices;

                            using vectorVector3 normals = geometryData.normals;
                            if (normals != null)
                            {
                                using vectorVector3 newNormals = new vectorVector3();
                                using var dataNormals = geometryData.normals;
                                foreach (Vector3 normal in dataNormals)
                                {
                                    using (normal)
                                    {
                                        using Vector3 newNormal = rotation.opMult(normal);
                                        newNormals.Add(newNormal);
                                    }
                                }
                                geometryData.normals = newNormals;
                            }
                            geometryData.UpdateBounds();
                            try // Non-vital
                            {
                                // TODO is this the right mapping?
                                geometryData.CalcTangentSpace();
                                // TriShapeData.UpdateTangents;
                            }
                            catch (Exception e)
                            {
                                meshHandler._settings.diagnostics.logger.WriteLine("Exception when updating the Tangent for the left-hand variant(s) for NiGeometry {0} in {1} : {2}",
                                    id, nifPath, e.GetBaseException());
                            }
                        }
                    }
                }
            }

            // Clear the new transform of elements applied above
            using MatTransform newTransform = new MatTransform();
            Matrix3 newRotation = Matrix3.MakeRotation(0.0f, 0.0f, 0.0f);   // yaw, pitch, roll
            newTransform.rotation = newRotation;
            using Vector3 newTranslation = new Vector3();
            newTranslation.Zero();
            newTransform.translation = newTranslation;
            newTransform.scale = 1.0f;
            block.transform = newTransform;
        }

        // We treat the loaded source NIF data as a writable scratchpad, to ease mirroring of script logic
        internal void Generate()
        {
            // Populate the list of child blocks, have to use these to Apply Transforms from non-trishapes to their kids
            NiAVObject? scabbard = null;
            uint scabbardId = 0;
            using (NiNode rootNode = nif.GetRootNode())
            {
                if (rootNode == null)
                    return;

                var childNodes = rootNode.GetChildren().GetRefs();
                foreach (var childNode in childNodes)
                {
                    if (rootChildIds.Contains(childNode.index))
                        continue;
                    var block = blockCache.EditableBlockById<NiAVObject>(childNode.index);
                    if (block == null)
                        continue;
                    rootChildIds.Add(childNode.index);
                    using var blockName = block.name;
                    if (blockName.get().ToLower().Equals(ScbTag, StringComparison.OrdinalIgnoreCase))
                    {
                        scabbard = block;
                        scabbardId = childNode.index;
                    }

                    if (blockName.get().ToLower().Equals(ScbTag + LeftSuffix, StringComparison.OrdinalIgnoreCase))
                    {
                        meshHandler._settings.diagnostics.logger.WriteLine(
                            "Mesh {0} skipped: {1} block already present", nifPath, ScbTag + LeftSuffix);
                        return;
                    }
                }

                // Generate a hidden mirror of Scabbard if present
                if (scabbard == null)
                {
                    meshHandler._settings.diagnostics.logger.WriteLine("Mesh {0} skipped: {1} block not present", nifPath, ScbTag);
                    return;
                }

                // MESH #1
                meshHandler._settings.diagnostics.logger.WriteLine("Attempting to generate transformed Mesh for {0}", nifPath);

                // Transform Mesh in place
                string destPath = Path.GetFullPath(meshHandler._settings.meshes.OutputFolder + MeshHandler.MeshPrefix + nifPath);
                AddScabbardMirror(new HashSet<uint>(), scabbard, rootNode);

                //Save and finish
                nif.SafeSave(destPath, ScriptLess.saveOptions);

                meshHandler._settings.diagnostics.logger.WriteLine("Successfully added hidden scabbard mirror in {0}", destPath);
                Interlocked.Increment(ref meshHandler.countGenerated);
            }
        }
    }
}
