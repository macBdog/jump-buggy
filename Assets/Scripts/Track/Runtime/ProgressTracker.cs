﻿using System.Collections;
using System.Linq;
using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class ProgressTracker : MonoBehaviour
{
    // Components
    private Rigidbody carBody;

    [Header("Parameters")]
    public int CurveSearchAhead = 2;            // # of curves to search ahead when checking whether player has progressed to the next curve. Does not count jumps (which are skipped)
    public float OffRoadTimeout = 10.0f;        // # of seconds player is off the road before they will be placed back on.
    public bool AutoReset = true;               // Flag to auto-reset player

    // Working
    [Header("Working")]
    public int currentCurve = 0;
    public int lapCount = 0;
    public float offRoadTimer = 0.0f;

    [Header("Lap times")]
    public float LastLapTime = 0.0f;
    public float BestLapTime = 0.0f;
    public float CurrentLapTime = 0.0f;

    private bool isPuttingCarOnRoad = false;

    void Start()
    {
        carBody = GetComponent<Rigidbody>();
    }

    void FixedUpdate()
    {
        var road = Track.Instance;
        if (road == null || road.CurveInfos == null || !road.CurveInfos.Any()) return;

        // Search ahead to determine whether car is above the road, and which curve
        bool isAboveRoad = false;

        int curveIndex = currentCurve;
        for (int i = 0; i < CurveSearchAhead; i++)
        {
            // Ray cast from center of car in opposite direction to curve
            var ray = new Ray(carBody.transform.position, -road.CurveInfos[curveIndex].Normal);
            RaycastHit hit;
            if (Physics.Raycast(ray, out hit))
            {
                // RoadMeshInfo component indicates we've hit the road.
                var roadInfo = hit.transform.GetComponent<TrackSurface>();
                if (roadInfo != null && roadInfo.ContainsCurveIndex(curveIndex))
                {
                    if (curveIndex < currentCurve)
                        LapCompleted();
                    currentCurve = curveIndex;
                    isAboveRoad = true;
                }
            }

            // Find next non-jump curve to check
            int prevCurveIndex = curveIndex;            // (Detect if we loop all the way around)
            do
            {
                curveIndex = (curveIndex + 1) % road.CurveInfos.Length;
            } while (curveIndex != prevCurveIndex && road.CurveInfos[curveIndex].IsJump);
        }

        // Off-road timer logic
        if (isAboveRoad || !AutoReset)
        {
            offRoadTimer = 0.0f;
        }
        else
        {
            offRoadTimer += Time.fixedDeltaTime;
            if (offRoadTimer > OffRoadTimeout)
                StartCoroutine(PutCarOnRoadWithFade());
        }

        // Update lap timer
        CurrentLapTime += Time.fixedDeltaTime;
    }

    public IEnumerator PutCarOnRoadWithFade()
    {
        // Abort if already in the process of placing the car on the road
        if (isPuttingCarOnRoad)
            return CoroutineUtils.EmptyCoroutine();

        isPuttingCarOnRoad = true;
        return CoroutineUtils.FadeOut()
            .Then(() => {
                PutCarOnRoad();
                isPuttingCarOnRoad = false;
            })
            .Then(CoroutineUtils.FadeIn());
    }

    public void PutCarOnRoad()
    {
        var road = Track.Instance;

        // Search backwards from current curve for a respawnable curve. Don't go back past 
        // the start of the track though (otherwise player could clock up an extra lap).
        int curveIndex = currentCurve;
        while (curveIndex > 0 && !road.CurveInfos[curveIndex].CanRespawn)
            curveIndex--;

        // Position player at spawn point.
        // Spawn point is in track space, so must transform to get world space.
        var curveInfo = road.CurveInfos[curveIndex];
        transform.position = road.transform.TransformPoint(curveInfo.RespawnPosition);
        transform.rotation = road.transform.rotation * curveInfo.RespawnRotation;

        // Kill all linear and angular velocity
        if (carBody != null)
        {
            carBody.velocity = Vector3.zero;
            carBody.angularVelocity = Vector3.zero;
        }

        // Reset state
        offRoadTimer = 0.0f;
        currentCurve = curveIndex;
    }

    private void LapCompleted()
    {
        lapCount++;

        // Update lap times
        LastLapTime = CurrentLapTime;
        CurrentLapTime = 0.0f;
        if (BestLapTime == 0.0f || LastLapTime < BestLapTime)
            BestLapTime = LastLapTime;
    }
}
