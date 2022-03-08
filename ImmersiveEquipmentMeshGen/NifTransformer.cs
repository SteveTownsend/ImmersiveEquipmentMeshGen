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
        string destPath;

        //bool meshHasController;

        ISet<uint> rootChildIds = new SortedSet<uint>();

        internal NifTransformer(MeshHandler handler, NifFile source, string modelPath, string newPath, ModelType modelType, WeaponType weaponType)
        {
            meshHandler = handler;
            nif = source;
            blockCache = new niflycpp.BlockCache(nif.GetHeader());
            header = blockCache.Header;
            nifPath = modelPath;
            destPath = newPath;
            nifModel = modelType;
            nifWeapon = weaponType;
        }

        public void Dispose()
        {
            blockCache.Dispose();
        }

        private void ApplyTransformToChild(NiAVObject parent, NiAVObject child, uint childId, bool isBow)
        {
            using MatTransform cTransform = child.transform;
            using MatTransform pTransform = parent.transform;
            using MatTransform transform = pTransform.ComposeTransforms(cTransform);
            child.transform = transform;

            // Don't do this for shapes, Don't remove Transforms of Shapes in case they need to be mirrored
            /*if (child.controllerRef != null && !child.controllerRef.IsEmpty())
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
            }*/
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

        // Rename scabbard and any children, we are copying the entire tree mirrored into the dest mesh
        private bool AddScabbardMirror(string nifPath, ISet<uint> alreadyDone, NiAVObject source, NiNode parent, bool scbRoot)
        {
            using NiAVObject blockDest = niflycpp.BlockCache.SafeClone<NiAVObject>(source);

            using var blockName = source.name;
            string newName = blockName.get() + LeftSuffix;
            uint newId = header.AddOrFindStringId(newName);
            NiStringRef newRef = new NiStringRef(newName);
            newRef.SetIndex(newId);
            blockDest.name = newRef;

            // Hide the mirror (just the root), Immersive Equipment Display/Simple Dual Sheath will unhide as needed
            if (scbRoot)
            {
                blockDest.flags |= 0x1;
            }

            if (blockDest is BSTriShape || blockDest is NiTriShape || blockDest is NiTriStrips)
            {
                // seems that legacy trishapes aren't cloned properly, skip for now
                if (blockDest is not BSTriShape)
                    return false;

                // TODO it appears these functions could be combined. Stick with script flow for safety, at least initially.
                ApplyTransform(newId, blockDest); //In case things are at an angle where flipping Z would produce incorrect results.
                FlipAlongZ(newId, blockDest);
            }
            else
            {
                NiNode? destNode = blockDest as NiNode;
                if (destNode is not null)
                {
                    // Copy Blocks all the way down until a trishape is reached
                    using var childNodes = destNode.CopyChildRefs();

                    destNode.childRefs = new NiBlockRefArrayNiAVObject();

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
                            meshHandler._settings.diagnostics.logger.WriteLine("{0}: AddScabbardMirror checking Child {1}", nifPath, childNode.index);
                            
                            if (!AddScabbardMirror(nifPath, alreadyDone, block, destNode, false))
                            {
                                return false;
                            }
                        }
                    }

                }
            }

            // scb root should have atleast one child
            if (scbRoot)
            {
                NiNode? node = blockDest as NiNode;
                if (node is not null)
                {
                    if (node.GetChildren().GetSize() == 0)
                    {
                        meshHandler._settings.diagnostics.logger.WriteLine("{0}: AddScabbardMirror - empty root scb node", nifPath);

                        return false;
                    }
                }
            }

            // Insert new block in the mesh once all editing due to child content is complete
            header.AddBlock(blockDest);
            nif.SetParentNode(blockDest, parent);

            return true;
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
                        // skip hidden scabbard nodes
                        if ((block.flags & 0x1) == 0x1)
                        {
                            meshHandler._settings.diagnostics.logger.WriteLine(
                                "Mesh {0} skipped: {1} block is hidden", nifPath, ScbTag);
                            return;
                        }

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
                if (!AddScabbardMirror(nifPath, new HashSet<uint>(), scabbard, rootNode, true))
                {
                    meshHandler._settings.diagnostics.logger.WriteLine("{0}: AddScabbardMirror returned false", nifPath);
                    return;
                }

                //Save and finish
                nif.SafeSave(destPath, ScriptLess.saveOptions);

                meshHandler._settings.diagnostics.logger.WriteLine("Successfully added hidden scabbard mirror in {0}", destPath);
                Interlocked.Increment(ref meshHandler.countGenerated);
            }
        }
    }
}
