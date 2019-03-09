﻿using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// The main racetrack component.
/// Manages a collection of RacetrackCurve curves. Warps meshes around them to create 
/// the visual and physical racetrack model.
/// </summary>
[ExecuteInEditMode]
public class Racetrack : MonoBehaviour {

    private const int MaxSpacingGroups = 16;

    [Header("Parameters")]
    public float SegmentLength = 0.25f;
    public float RespawnHeight = 0.75f;

    /// <summary>
    /// Runtime information for each curve. Used by RacetrackProgressTracker.
    /// </summary>
    [HideInInspector]
    public CurveRuntimeInfo[] CurveInfos;

    /// <summary>
    /// Track building state at the start of each curve.
    /// Allows rebuilding meshes for just a single curve or sub-range.
    /// </summary>
    [HideInInspector]
    public BuildMeshesState[] CurveBuildMeshState;

    // Working

    /// <summary>
    /// Curves processed into a set of small linear segments
    /// </summary>
    private List<Segment> segments;

    /// <summary>
    /// Singleton instance
    /// </summary>
    public static Racetrack Instance;

    private void Awake()
    {
        Instance = this;
    }

    void Start()
    {
        // Ensure segment and curve runtime information arrays are populated.
        segments = GenerateSegments().ToList();
        PositionCurves();
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    /// <summary>
    /// Recreate all meshes
    /// </summary>
    public void CreateMeshes()
    {
        CreateMeshes(0, Curves.Count);
    }

    /// <summary>
    /// Recreate a range of meshes
    /// </summary>
    /// <param name="startCurveIndex">INCLUSIVE index of first curve</param>
    /// <param name="endCurveIndex">EXCLUSIVE index of last curve</param>
    public void CreateMeshes(int startCurveIndex, int endCurveIndex)
    {
        // Delete any previous meshes
        DeleteMeshes(startCurveIndex, endCurveIndex);

        // Ensure segments array and curve positions are up to date
        segments = GenerateSegments().ToList();
        PositionCurves();

        // Create new meshes
        BuildMeshes(startCurveIndex, endCurveIndex);
    }

    /// <summary>
    /// Clear mesh templates from each curve
    /// </summary>
    public void RemoveTemplates()
    {
        var curves = Curves;
        foreach (var curve in curves)
            curve.Template = null;
        DeleteMeshes();
    }

    /// <summary>
    /// Add a curve to the end of the track.
    /// Copies previous curve settings. Automatically builds meshes.
    /// </summary>
    /// <returns>The new curve</returns>
    public RacetrackCurve AddCurve()
    {
        var curves = Curves;
        var lastCurve = curves.LastOrDefault();

        // Add curve to end of list
        var obj = new GameObject();
        obj.transform.parent = transform;
        obj.name = "Curve";
        obj.isStatic = gameObject.isStatic;
        var curve = obj.AddComponent<RacetrackCurve>();
        curve.Index = curves.Count;

        // Copy values from last curve
        if (lastCurve != null)
        {
            curve.Length = lastCurve.Length;
            curve.Angles = lastCurve.Angles;
            curve.IsJump = lastCurve.IsJump;
            curve.CanRespawn = lastCurve.CanRespawn;
        }

        // Create curve meshes
        CreateMeshes(curve.Index - 1, curve.Index + 1);

        return curve;
    }

    /// <summary>
    /// Delete meshes from all curves
    /// </summary>
    public void DeleteMeshes()
    {
        DeleteMeshes(0, Curves.Count);
    }

    /// <summary>
    /// Delete meshes from a range of curves
    /// </summary>
    /// <param name="startCurveIndex">INCLUSIVE index of first curve</param>
    /// <param name="endCurveIndex">EXCLUSIVE index of last curve</param>
    public void DeleteMeshes(int startCurveIndex, int endCurveIndex)
    {
        // Find generated meshes.
        // These have a RacetrackTemplateCopy component
        var children = Curves
            .Where(c => c.Index >= startCurveIndex && c.Index < endCurveIndex)
            .SelectMany(c => c.gameObject.GetComponentsInChildren<RacetrackTemplateCopy>())
            .ToList();

        // Delete them
        foreach (var child in children)
        {
            if (Application.isEditor)
                DestroyImmediate(child.gameObject);
            else
                Destroy(child.gameObject);
        }
    }

    /// <summary>
    /// Calculate each curve's position.
    /// Also builds the curve runtime information array.
    /// </summary>
    public void PositionCurves()
    {
        // Position curve objects at the start of their curves
        var curves = Curves;
        CurveInfos = new CurveRuntimeInfo[curves.Count];
        float curveZOffset = 0.0f;
        int index = 0;
        foreach (var curve in curves)
        {
            // Position curve at first segment
            int segIndex = Mathf.FloorToInt(curveZOffset / SegmentLength);
            var seg = GetSegment(segIndex);
            GetSegmentTransform(seg, curve.transform);
            curve.Index = index;

            // Calculate curve runtime info
            var info = new CurveRuntimeInfo();
            info.IsJump = curve.IsJump;
            info.CanRespawn = curve.CanRespawn;
            info.zOffset = curveZOffset;

            // Calculate normal in track space
            int endSegIndex = Mathf.FloorToInt((curveZOffset + curve.Length) / SegmentLength);
            int midSegIndex = (segIndex + endSegIndex) / 2;
            info.Normal = GetSegment(midSegIndex).GetSegmentToTrack().MultiplyVector(Vector3.up);

            // Calculate respawn point and direction vectors in track space
            int respawnSeg = Math.Min(segIndex + Mathf.CeilToInt(2.0f / SegmentLength), midSegIndex);
            Matrix4x4 respawnTransform = GetSegment(respawnSeg).GetSegmentToTrack();
            info.RespawnPosition = respawnTransform.MultiplyPoint(Vector3.up * RespawnHeight);
            info.RespawnRotation = Quaternion.LookRotation(
                respawnTransform.MultiplyVector(Vector3.forward).normalized,
                respawnTransform.MultiplyVector(Vector3.up).normalized);

            CurveInfos[index] = info;

            // Move on to next curve
            index++;
            curveZOffset += curve.Length;
        }
    }

    /// <summary>
    /// Rebuild segments array
    /// </summary>
    public void UpdateSegments()
    {
        segments = GenerateSegments().ToList();
    }

    /// <summary>
    /// Get segment array for single curve.
    /// Used by RacetrackCurveEditor to display curves in scene GUI
    /// </summary>
    /// <param name="curve">The racetrack curve</param>
    /// <returns>Enumerable of segments for the specified curve</returns>
    public IEnumerable<Segment> GetCurveSegments(RacetrackCurve curve)
    {
        if (segments == null) segments = GenerateSegments().ToList();

        // Find start of curve
        int i = 0;
        while (i < segments.Count && segments[i].Curve.Index < curve.Index)
            i++;

        // Return curve segments
        while (i < segments.Count && segments[i].Curve.Index == curve.Index)
        {
            yield return segments[i];
            i++;
        }
    }

    /// <summary>
    /// Get all segments.
    /// Used by RacetrackCurveEditor to display curves in scene GUI
    /// </summary>
    /// <returns>Enumerable of all segments across all curves</returns>
    public List<Segment> GetSegments()
    {
        if (segments == null) segments = GenerateSegments().ToList();
        return segments;
    }

    /// <summary>
    /// Build meshes for all curves
    /// </summary>
    private void BuildMeshes()
    {
        BuildMeshes(0, Curves.Count);
    }

    /// <summary>
    /// Build meshes for range of curves
    /// </summary>
    /// <param name="startCurveIndex">INCLUSIVE start curve index</param>
    /// <param name="endCurveIndex">EXCLUSIVE end curve index</param>
    private void BuildMeshes(int startCurveIndex, int endCurveIndex)
    {
        if (!segments.Any()) return;
        var curves = Curves;

        // Clamp curve range
        if (startCurveIndex < 0)
            startCurveIndex = 0;
        if (endCurveIndex > curves.Count)
            endCurveIndex = curves.Count;

        // Create/update build mesh state array in parallel
        if (CurveBuildMeshState == null)
            CurveBuildMeshState = new BuildMeshesState[curves.Count];
        else
            Array.Resize(ref CurveBuildMeshState, curves.Count);

        // Fetch state from previous curve and unpack.
        BuildMeshesState state = startCurveIndex > 0 ? CurveBuildMeshState[startCurveIndex - 1] : new BuildMeshesState();
        var spacingGroupStates = state.SpacingGroups.Select(g => new SpacingGroupState
        {
            IsActive = g.IsActive,
            ZOffset = g.ZOffset
        }).ToArray();
        float meshZOffset = state.MeshZOffset;
        RacetrackMeshTemplate template = state.Template;

        // Work down the curve. Add meshes as we go.
        var totalLength = segments.Count * SegmentLength;
        var curveIndex = startCurveIndex;
        while (meshZOffset < totalLength)
        {
            // Find segment where mesh starts
            int segIndex = Mathf.FloorToInt(meshZOffset / SegmentLength);
            var seg = segments[segIndex];
            var curve = seg.Curve;

            // Store state at end of each curve
            while (seg.Curve.Index > curveIndex) {
                CurveBuildMeshState[curveIndex] = new BuildMeshesState
                {
                    SpacingGroups = spacingGroupStates.Select(s => new SpacingGroupBaseState
                    {
                        IsActive = s.IsActive,
                        ZOffset = s.ZOffset
                    }).ToArray(),
                    Template = template,
                    MeshZOffset = meshZOffset
                };
                curveIndex++;
            }

            // Stop if end curve reached
            if (curve.Index >= endCurveIndex) break;

            // Look for mesh template
            if (curve.Template != null)
                template = curve.Template;

            // Initialise spacing groups for this template
            foreach (var group in spacingGroupStates)
                group.IsActiveThisTemplate = false;

            // Generate meshes
            if (!curve.IsJump && template != null)
            {
                // Create template copy object
                var templateCopyObj = new GameObject();
                templateCopyObj.transform.parent = curve.transform;
                templateCopyObj.isStatic = gameObject.isStatic;
                templateCopyObj.name = curve.name + " > " + template.name;
                GetSegmentTransform(seg, templateCopyObj.transform);

                // Add template copy component
                var templateCopy = templateCopyObj.AddComponent<RacetrackTemplateCopy>();
                templateCopy.Template = template;

                // Pass 1: Generate continuous meshes
                bool isFirstMesh = true;
                float mainMeshLength = 0.0f;
                foreach (var subtree in template.FindSubtrees<RacetrackContinuous>())
                {
                    // Duplicate subtree
                    var subtreeCopy = Instantiate(subtree);
                    subtreeCopy.transform.parent = templateCopyObj.transform;
                    subtreeCopy.gameObject.isStatic = gameObject.isStatic;
                    subtreeCopy.name += " Continuous";
                    GetSegmentTransform(seg, subtreeCopy.transform);

                    // Need to take into account relative position of continuous subtree within template object
                    // Note: The subtree's transformation is effectively cancelled out, however we multiply back in the scale (lossyScale)
                    // as this allows setting the scale on the template object itself, which is useful.
                    // (Otherwise you would have to create a sub-object in order to perform scaling)
                    Matrix4x4 templateFromSubtree = Matrix4x4.Scale(template.transform.lossyScale) * template.transform.localToWorldMatrix.inverse * subtree.transform.localToWorldMatrix;

                    // Clone and warp displayed meshes
                    var meshFilters = subtreeCopy.GetComponentsInChildren<MeshFilter>();
                    foreach (var mf in meshFilters)
                    {
                        mf.sharedMesh = CloneMesh(mf.sharedMesh);
                        Matrix4x4 subtreeFromMesh = subtreeCopy.transform.localToWorldMatrix.inverse * mf.transform.localToWorldMatrix;
                        Matrix4x4 templateFromMesh = templateFromSubtree * subtreeFromMesh;
                        Matrix4x4 meshFromWorld = mf.transform.localToWorldMatrix.inverse;
                        float meshLength = WarpMeshToCurves(mf.sharedMesh, meshZOffset, templateFromMesh, meshFromWorld);
#if UNITY_EDITOR
                        Unwrapping.GenerateSecondaryUVSet(mf.sharedMesh);
#endif

                        // First continuous mesh is considered to be the main track surface,
                        // and determines the length of the template copy
                        if (isFirstMesh)
                        {
                            var surface = mf.gameObject.AddComponent<RacetrackSurface>();
                            int endSegIndex = Mathf.FloorToInt((meshZOffset + meshLength) / SegmentLength - 0.00001f);
                            Segment endSeg = segments[Math.Min(endSegIndex, segments.Count - 1)];
                            surface.StartCurveIndex = seg.Curve.Index;
                            surface.EndCurveIndex = endSeg.Curve.Index;
                            mainMeshLength = meshLength;
                            isFirstMesh = false;
                        }
                    }

                    // Clone and warp mesh colliders
                    var meshColliders = subtreeCopy.GetComponentsInChildren<MeshCollider>();
                    foreach (var mc in meshColliders)
                    {
                        if (mc.sharedMesh == null) continue;
                        mc.sharedMesh = CloneMesh(mc.sharedMesh);
                        Matrix4x4 subtreeFromMesh = subtreeCopy.transform.localToWorldMatrix.inverse * mc.transform.localToWorldMatrix;
                        Matrix4x4 templateFromMesh = templateFromSubtree * subtreeFromMesh;
                        Matrix4x4 meshFromWorld = mc.transform.localToWorldMatrix.inverse;
                        float meshLength = WarpMeshToCurves(mc.sharedMesh, meshZOffset, templateFromMesh, meshFromWorld);
                    }
                };

                // Pass 2: Generate spaced meshes
                foreach (var subtree in template.FindSubtrees<RacetrackSpaced>())
                {
                    // Search up parent chain for spacing group
                    var spacingGroup = subtree.GetComponentsInParent<RacetrackSpacingGroup>(true).FirstOrDefault();
                    if (spacingGroup == null)
                    {
                        Debug.LogError("Cannot find spacing group for spaced template component: " + subtree.name);
                        continue;
                    }

                    // Validate
                    if (spacingGroup.Index < 0 || spacingGroup.Index >= MaxSpacingGroups)
                    {
                        Debug.LogError("Invalid spacing group " + spacingGroup.Index + " found in template: " + template.name);
                        continue;
                    }
                    if (spacingGroup.Spacing < SegmentLength)
                    {
                        Debug.LogError("Spacing too small in spacing group, in template: " + template.name);
                        continue;
                    }

                    // Activate spacing group
                    var groupState = spacingGroupStates[spacingGroup.Index];
                    groupState.IsActiveThisTemplate = true;
                    if (!groupState.IsActive)
                        groupState.ZOffset = meshZOffset;

                    // Walk spacing group forward to current curve
                    groupState.ZOffsetThisTemplate = groupState.ZOffset;
                    while (groupState.ZOffsetThisTemplate + spacingGroup.SpacingBefore < meshZOffset)
                        groupState.ZOffsetThisTemplate += spacingGroup.Spacing;

                    // Generate spaced objects for current curve
                    while (groupState.ZOffsetThisTemplate + spacingGroup.SpacingBefore < meshZOffset + mainMeshLength)
                    {
                        float spaceZOffset = groupState.ZOffsetThisTemplate + spacingGroup.SpacingBefore;
                        var spaceSegIndex = Mathf.FloorToInt(spaceZOffset / SegmentLength);
                        var spaceSeg = GetSegment(spaceSegIndex);

                        // Check track angle restrictions
                        float trackXAngle = Mathf.Abs(RacetrackUtil.LocalAngle(spaceSeg.Direction.x));
                        float trackZAngle = Mathf.Abs(RacetrackUtil.LocalAngle(spaceSeg.Direction.z));
                        if (trackXAngle > subtree.MaxXAngle || trackZAngle > subtree.MaxZAngle)
                        {
                            groupState.ZOffsetThisTemplate += spacingGroup.Spacing;                         // Outside angle restrictions. Skip creating this one
                            continue;                   
                        }

                        // Duplicate subtree
                        var subtreeCopy = Instantiate(subtree);
                        subtreeCopy.transform.parent = templateCopyObj.transform;
                        subtreeCopy.gameObject.isStatic = gameObject.isStatic;
                        subtreeCopy.name += " Spacing group " + spacingGroup.Index;

                        // Calculate local to track tranform matrix for subtree.
                        Matrix4x4 templateFromSubtree = Matrix4x4.Scale(template.transform.lossyScale) * template.transform.localToWorldMatrix.inverse * subtree.transform.localToWorldMatrix;
                        Matrix4x4 trackFromSubtree = 
                              spaceSeg.GetSegmentToTrack(spaceZOffset - spaceSegIndex * SegmentLength)      // Segment -> Track
                            * templateFromSubtree;                                                          // Subtree -> Segment

                        if (subtree.IsVertical)
                        {
                            // Disassemble matrix
                            Vector3 basisX = RacetrackUtil.ToVector3(trackFromSubtree.GetRow(0));
                            Vector3 basisY = RacetrackUtil.ToVector3(trackFromSubtree.GetRow(1));
                            Vector3 basisZ = RacetrackUtil.ToVector3(trackFromSubtree.GetRow(2));

                            // Align Y vector with global Y axis
                            basisY = Vector3.up * basisY.magnitude;

                            // Cross product to get X and Z (assuming matrix is orthogonal)
                            basisX = Vector3.Cross(basisY, basisZ).normalized * basisX.magnitude;
                            basisZ = Vector3.Cross(basisX, basisY).normalized * basisZ.magnitude;

                            // Recompose matrix
                            trackFromSubtree.SetColumn(0, RacetrackUtil.ToVector4(basisX));
                            trackFromSubtree.SetColumn(1, RacetrackUtil.ToVector4(basisY));
                            trackFromSubtree.SetColumn(2, RacetrackUtil.ToVector4(basisZ));
                        }

                        // Get local to world transform matrix for subtree.
                        Matrix4x4 subtreeTransform = transform.localToWorldMatrix                           // Track -> World
                            * trackFromSubtree;                                                             // Subtree -> Track

                        // Calculate local transform. Essentially subtree->template space transform
                        Matrix4x4 localTransform = templateCopyObj.transform.localToWorldMatrix.inverse     // World -> template
                            * subtreeTransform;                                                             // Subtree -> World

                        // Use to set transform
                        subtreeCopy.transform.localPosition = localTransform.MultiplyPoint(Vector3.zero);
                        subtreeCopy.transform.localRotation = localTransform.rotation;
                        subtreeCopy.transform.localScale    = localTransform.lossyScale;                        

                        // Move on to next
                        groupState.ZOffsetThisTemplate += spacingGroup.Spacing;
                    }
                }

                // Move forward to start of next mesh template
                meshZOffset += mainMeshLength;
            }

            // Update spacing group active flags
            foreach (var group in spacingGroupStates)
            {
                group.IsActive = group.IsActiveThisTemplate;
                group.ZOffset = group.ZOffsetThisTemplate;
            }

            // Ensure Z offset is advanced at least to the next segment.
            // (Otherwise 0 length templates would cause an infinite loop)
            meshZOffset = Mathf.Max(meshZOffset, (segIndex + 1) * SegmentLength);
        }
    }

    /// <summary>
    /// Warp a single mesh along the racetrack curves
    /// </summary>
    /// <param name="mesh">The mesh to warp. Vertex, normal and tangent arrays will be cloned and modified.</param>
    /// <param name="meshZOffset">The distance along all curves where mesh begins</param>
    /// <param name="meshToTemplate">Transformation from mesh space to mesh template space</param>
    /// <param name="worldToMesh">Transformation from world space to mesh space</param>
    /// <returns>Length of mesh in template space</returns>
    private float WarpMeshToCurves(Mesh mesh, float meshZOffset, Matrix4x4 meshToTemplate, Matrix4x4 worldToMesh)
    {
        // Find length of mesh            
        float meshMaxZ = mesh.vertices.Max(v => meshToTemplate.MultiplyPoint(v).z);
        float meshMinZ = mesh.vertices.Min(v => meshToTemplate.MultiplyPoint(v).z);
        float meshLength = meshMaxZ - meshMinZ;

        // Lookup first segment
        int segIndex = Mathf.FloorToInt(meshZOffset / SegmentLength);
        Segment seg = segments[segIndex];

        // Warp vertices around road curve
        Debug.Assert(mesh.vertices.Length == mesh.normals.Length);
        Debug.Assert(mesh.vertices.Length == mesh.tangents.Length);
        var vertices = new Vector3[mesh.vertices.Length];
        var normals = new Vector3[mesh.normals.Length];
        for (int i = 0; i < mesh.vertices.Length; i++)
        {
            Vector3 v = meshToTemplate.MultiplyPoint(mesh.vertices[i]);
            Vector3 n = meshToTemplate.MultiplyVector(mesh.normals[i]);

            // z determines index in meshSegments array
            float z = v.z - meshMinZ + meshZOffset;                                         // Total Z along all curves
            var vertSegIndex = Mathf.FloorToInt(z / SegmentLength);
            var vertSeg = GetSegment(vertSegIndex);

            // Calculate warped position
            Vector3 segPos = new Vector3(v.x, v.y, z - vertSegIndex * SegmentLength);       // Position in segment space
            Matrix4x4 segToTrack = vertSeg.GetSegmentToTrack(segPos.z);
            Vector3 trackPos = segToTrack.MultiplyPoint(segPos);                            // => Track space
            Vector3 worldPos = transform.TransformPoint(trackPos);                          // => World space
            vertices[i] = worldToMesh.MultiplyPoint(worldPos);                              // => Mesh space

            // Warp normal
            Vector3 segNorm = n;                                                            // Normal in segment space
            Vector3 trackNorm = segToTrack.MultiplyVector(segNorm);                         // => Track space
            Vector3 worldNorm = transform.TransformVector(trackNorm);                       // => World space
            normals[i] = worldToMesh.MultiplyVector(worldNorm).normalized;                  // => Mesh space
        }

        // Replace mesh vertices and normals
        mesh.vertices = vertices;
        mesh.normals = normals;

        // Recalculate tangents and bounds
        mesh.RecalculateTangents();
        mesh.RecalculateBounds();

        return meshLength;
    }

    /// <summary>
    /// Get transformation for object positioned at a given segment
    /// </summary>
    /// <param name="seg">The segment</param>
    /// <param name="dstTransform">The transform to update</param>
    private void GetSegmentTransform(Segment seg, Transform dstTransform)
    {
        // Set transform to position object at start of segment, with 
        // Y axis rotation set (but not X and Z).

        // Segment gives position and rotation in track space.
        var segmentToTrack = seg.GetSegmentToTrack(0.0f);
        var segPos = segmentToTrack.MultiplyPoint(Vector3.zero);
        var segForward = segmentToTrack.MultiplyVector(Vector3.forward);

        // Convert to world space
        var worldPos = transform.TransformPoint(segPos);
        var worldForward = transform.TransformVector(segForward);

        // Set transform
        dstTransform.position = worldPos;
        dstTransform.rotation = Quaternion.LookRotation(worldForward);
    }

    /// <summary>
    /// Generate segments from track curves
    /// </summary>
    /// <returns>Enumerable of segments for all curves</returns>
    private IEnumerable<Segment> GenerateSegments()
    {
        // Find curves. Must be in immediate children.
        var curves = Curves;

        // Walk along curve in track space.
        Vector3 pos = Vector3.zero;
        Vector3 dir = Vector3.zero;

        Vector3 dirDelta = Vector3.zero;
        Vector3 posDelta = Vector3.forward * SegmentLength;
        foreach (var curve in curves)
        {
            // Find delta to add to curve each segment
            dirDelta = new Vector3(
                RacetrackUtil.LocalAngle(curve.Angles.x - dir.x),
                curve.Angles.y,
                RacetrackUtil.LocalAngle(curve.Angles.z - dir.z)
            ) / curve.Length * SegmentLength;

            // Generate segments
            for (float d = 0.0f; d < curve.Length; d += SegmentLength)
            {
                var segment = new Segment
                {
                    Position = pos,
                    Direction = dir,
                    DirectionDelta = dirDelta,
                    Length = SegmentLength,
                    Curve = curve
                };
                yield return segment;

                // Advance to start of next segment
                pos += segment.GetSegmentToTrack(0.0f).MultiplyVector(posDelta);
                dir += dirDelta;
            }
        }

        // Return final segment
        yield return new Segment
        {
            Position = pos,
            Direction = dir,
            DirectionDelta = dirDelta,
            Length = SegmentLength,
            Curve = curves.LastOrDefault()
        };
    }

    /// <summary>
    /// Get segment by index. Generate "virtual" segments to handle array overrun.
    /// </summary>
    /// <param name="i">Index into segments array</param>
    /// <returns>Corresponding segment.</returns>
    public Segment GetSegment(int i)
    {
        if (i < 0) return segments[0];
        if (i < segments.Count) return segments[i];

        // It's likely meshes won't exactly add up to the Z length of the curves, so the last one will overhang.
        // We allow for this by generating a virtual segment extruded from the last segment in the list.
        var lastSeg = segments[segments.Count - 1];
        return new Segment
        {
            Position = lastSeg.Position + lastSeg.GetSegmentToTrack(0.0f).MultiplyVector(Vector3.forward * lastSeg.Length * (i - segments.Count)),
            Direction = lastSeg.Direction,
            DirectionDelta = Vector3.zero,
            Length = lastSeg.Length,
            Curve = lastSeg.Curve
        };
    }

    private List<RacetrackCurve> cachedCurves;

    /// <summary>
    /// Get list of curves in order.
    /// </summary>
    /// <remarks>
    /// Curve objects are immediate child objects with the RacetrackCurve component.
    /// (Typically they are added with the "Add Curve" editor buttons, rather than 
    /// created manually).
    /// </remarks>
    public List<RacetrackCurve> Curves
    {
        get
        {
            // Assume at runtime that the curves will not be modified, and we can cache them in a local field.
            // When game is not running (i.e. in editor) we always recalculate the curves
            if (cachedCurves == null || !Application.IsPlaying(gameObject))
                cachedCurves = Enumerable.Range(0, gameObject.transform.childCount)
                            .Select(i => gameObject.transform.GetChild(i).GetComponent<RacetrackCurve>())
                            .Where(c => c != null)
                            .ToList();
            return cachedCurves;
        }
    }

    /// <summary>
    /// Shallow copy mesh object
    /// </summary>
    private static Mesh CloneMesh(Mesh src)
    {
        // Note: This doesn't copy all fields, just the ones we use
        var dst = new Mesh()
        {
            name = src.name + " clone",
            vertices = src.vertices,
            normals = src.normals,
            tangents = src.tangents,
            uv = src.uv,
            uv2 = src.uv2,
            uv3 = src.uv3,
            uv4 = src.uv4,
            uv5 = src.uv5,
            uv6 = src.uv6,
            uv7 = src.uv7,
            uv8 = src.uv8,
            triangles = src.triangles,
            colors = src.colors,
            colors32 = src.colors32,
            bounds = src.bounds,
            subMeshCount = src.subMeshCount,
            indexFormat = src.indexFormat,
            bindposes = src.bindposes,
            boneWeights = src.boneWeights,
            hideFlags = src.hideFlags            
        };
        for (int i = 0; i < src.subMeshCount; i++)
            dst.SetTriangles(src.GetTriangles(i), i);
        return dst;
    }

    /// <summary>
    /// Track curves are broken down into tiny straight segments.
    /// </summary>
    public class Segment
    {
        public Vector3 Position;
        public Vector3 Direction;           // Direction as Euler angles
        public Vector3 DirectionDelta;      // Added to Direction to get next segment's direction (and used to lerp between them)
        public float Length;                // Copy of Racetrack.SegmentLength for convenience
        public RacetrackCurve Curve;        // Curve to which segment belongs

        /// <summary>
        /// Get matrix converting from segment space to racetrack space
        /// </summary>
        /// <param name="segZ">Z distance along segment. [0, Length]</param>
        /// <returns>A transformation matrix</returns>
        public Matrix4x4 GetSegmentToTrack(float segZ = 0.0f)
        {
            float f = segZ / Length;                                                            // Fractional distance along segment
            float adjZDir = Direction.z + DirectionDelta.z * f;                                 // Adjust Z axis rotation based on distance down segment
            Vector3 adjDir = new Vector3(Direction.x, Direction.y, adjZDir);
            return Matrix4x4.Translate(Position) * Matrix4x4.Rotate(Quaternion.Euler(adjDir));
        }
    }

    /// <summary>
    /// Spacing group persisted state.
    /// See RacetrackSpacingGroup class.
    /// </summary>
    [Serializable]
    public class SpacingGroupBaseState
    {
        public bool IsActive = false;
        public float ZOffset = 0.0f;
    }

    /// <summary>
    /// Spacing group working state
    /// </summary>
    public class SpacingGroupState : SpacingGroupBaseState
    {
        public bool IsActiveThisTemplate = false;
        public float ZOffsetThisTemplate = 0.0f;
    }

    /// <summary>
    /// Runtime information attached to a curve.
    /// Can be used to track player progress along racetrack, and respawn after crashes.
    /// See RacetracProgressTracker
    /// </summary>
    [Serializable]
    public class CurveRuntimeInfo
    {
        public Vector3 Normal;                  // Upward normal in middle of curve
        public Vector3 RespawnPosition;
        public Quaternion RespawnRotation;
        public bool IsJump;                     // Copy of RacetrackCurve.IsJump
        public bool CanRespawn;                 // Copy of RacetrackCurve.CanRespawn
        public float zOffset;
    }

    /// <summary>
    /// State for building meshes.
    /// </summary>
    /// <remarks>
    /// A copy of this state for each curve is stored in the Racetrack.CurveBuildMeshState array,
    /// so that meshes for a single curve (or range) can be built without having to rebuild the 
    /// whole track.
    /// </remarks>
    [Serializable]
    public class BuildMeshesState
    {
        public SpacingGroupBaseState[] SpacingGroups;
        public float MeshZOffset = 0.0f;
        public RacetrackMeshTemplate Template = null;

        public BuildMeshesState()
        {
            // Populate SpacingGroups array
            SpacingGroups = new SpacingGroupBaseState[MaxSpacingGroups];
            for (int i = 0; i < SpacingGroups.Length; i++)
                SpacingGroups[i] = new SpacingGroupBaseState();
        }
    }
}