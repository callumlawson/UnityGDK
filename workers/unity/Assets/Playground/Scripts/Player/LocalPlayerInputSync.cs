using System;
using Generated.Playground;
using Improbable.Gdk.Core;
using Unity.Entities;
using UnityEngine;

#region Diagnostic control

#pragma warning disable 649
// ReSharper disable UnassignedReadonlyField
// ReSharper disable UnusedMember.Global

#endregion

namespace Playground
{
    [UpdateInGroup(typeof(SpatialOSUpdateGroup))]
    internal class LocalPlayerInputSync : ComponentSystem
    {
        private struct PlayerInputData
        {
            public readonly int Length;
            public ComponentDataArray<PlayerInput.Component> ThePlayerInput;
            public ComponentDataArray<PlayerInput.EventSender.FireBullet> FireBulletEvent;
            public ComponentDataArray<PlayerInput.EventSender.FireRay> FireRayEvent;
            public ComponentDataArray<CameraTransform> CameraTransform;
            public ComponentDataArray<Authoritative<PlayerInput.Component>> PlayerInputAuthority;
        }

        [Inject] private PlayerInputData playerInputData;

        private const float MinInputChange = 0.01f;

        protected override void OnUpdate()
        {
            for (var i = 0; i < playerInputData.Length; i++)
            {
                var cameraTransform = playerInputData.CameraTransform[i];
                var forward = cameraTransform.Rotation * Vector3.up;
                var right = cameraTransform.Rotation * Vector3.right;
                var input = Input.GetAxisRaw("Horizontal") * right + Input.GetAxisRaw("Vertical") * forward;
                var isShiftDown = Input.GetKey(KeyCode.LeftShift);

                var oldPlayerInput = playerInputData.ThePlayerInput[i];

                if (Math.Abs(oldPlayerInput.Horizontal - input.x) > MinInputChange
                    || Math.Abs(oldPlayerInput.Vertical - input.z) > MinInputChange
                    || oldPlayerInput.Running != isShiftDown)
                {
                    var newPlayerInput = new PlayerInput.Component
                    {
                        Horizontal = input.x,
                        Vertical = input.z,
                        Running = isShiftDown
                    };
                    playerInputData.ThePlayerInput[i] = newPlayerInput;
                }

                if (Input.GetMouseButtonDown(0))
                {
                    playerInputData.FireBulletEvent[i].Events.Add(new Empty2());
                }

                if (Input.GetMouseButtonDown(1))
                {
                    playerInputData.FireRayEvent[i].Events.Add(new Empty2());
                }
            }
        }
    }
}
