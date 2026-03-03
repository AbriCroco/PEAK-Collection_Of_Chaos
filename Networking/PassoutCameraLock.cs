using System.Collections;
using UnityEngine;

public class PassoutCameraLock : MonoBehaviour
{
    bool active = false;
    float unlockTime = 0f;
    Vector3 savedPosition;
    Quaternion savedRotation;
    Camera mainCam = null!;

    void Awake()
    {
        mainCam = Object.FindFirstObjectByType<Camera>();
    }

    public void LockForDuration(float seconds, float captureDelay = 0.15f)
    {
        float targetUnlock = Time.time + seconds;
        if (targetUnlock > unlockTime) unlockTime = targetUnlock;
        StartCoroutine(CaptureAndEnable(captureDelay, seconds));
    }

    private IEnumerator CaptureAndEnable(float captureDelay, float seconds)
    {
        if (captureDelay > 0f) yield return new WaitForSeconds(captureDelay);

        mainCam = Object.FindFirstObjectByType<Camera>();
        if (mainCam == null) yield break;

        // Wait for camera transform to stabilize before capturing to avoid transient states.
        const float maxStabilityWait = 0.6f;
        const float sampleInterval = 0.05f;
        const int requiredStableSamples = 3;
        const float posThreshold = 0.02f;
        const float angThresholdDeg = 1.5f;

        Vector3 lastPos = mainCam.transform.position;
        Quaternion lastRot = mainCam.transform.rotation;
        int stableCount = 0;
        float start = Time.time;

        while (Time.time - start < maxStabilityWait)
        {
            yield return new WaitForSeconds(sampleInterval);

            mainCam = Object.FindFirstObjectByType<Camera>();
            if (mainCam == null) yield break;

            Vector3 curPos = mainCam.transform.position;
            Quaternion curRot = mainCam.transform.rotation;

            float posDelta = Vector3.Distance(curPos, lastPos);
            float angDelta = Quaternion.Angle(curRot, lastRot);

            if (posDelta <= posThreshold && angDelta <= angThresholdDeg)
            {
                stableCount++;
                if (stableCount >= requiredStableSamples)
                    break;
            }
            else
            {
                stableCount = 0;
            }

            lastPos = curPos;
            lastRot = curRot;
        }

        // Capture whichever transform the camera currently has (stable or last seen)
        mainCam = Object.FindFirstObjectByType<Camera>();
        if (mainCam == null) yield break;

        savedPosition = mainCam.transform.position;
        savedRotation = mainCam.transform.rotation;

        float targetUnlock = Time.time + seconds;
        if (targetUnlock > unlockTime) unlockTime = targetUnlock;

        active = true;
        enabled = true;
    }

    void LateUpdate()
    {
        if (!active || mainCam == null) return;

        mainCam.transform.SetPositionAndRotation(savedPosition, savedRotation);

        if (Time.time >= unlockTime) EndLock();
    }

    void EndLock()
    {
        active = false;
        enabled = false;
    }

    public void Unlock()
    {
        EndLock();
    }
}
