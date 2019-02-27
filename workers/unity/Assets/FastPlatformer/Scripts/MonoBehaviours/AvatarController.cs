using System;
using System.Collections.Generic;
using System.Xml;
using Gameschema.Untrusted;
using Improbable.Gdk.GameObjectRepresentation;
using JetBrains.Annotations;
using KinematicCharacterController;
using UnityEngine;
using AnimationEvent = Gameschema.Untrusted.AnimationEvent;

namespace FastPlatformer.Scripts.MonoBehaviours
{
    public class AvatarController : BaseCharacterController
    {
        [UsedImplicitly, Require] private PlayerVisualizerEvents.Requirable.Writer eventWriter;

        private enum JumpType
        {
            Single,
            Double,
            Tripple,
            Backflip,
            JumpPad
        }

        private struct JumpData
        {
            public JumpType JumpType;
            public float JumpSpeed;
            public SoundEvent JumpSound;
            public AnimationEvent JumpAnimation;
        }

        public enum JumpState
        {
            JumpStartedLastFrame,
            Ascent,
            Descent,
            JustLanded,
            Grounded
        }

        public enum DashState
        {
            Dashing,
            NotDashing
        }

        private enum GravityType
        {
            World,
            Object
        }

        //Not used yet. Next state - "sliding"
        public enum CharacterState
        {
            Default
        }

        public struct CharacterInputs
        {
            public float MoveAxisForward;
            public float MoveAxisRight;
            public Quaternion CameraRotation;
            public bool JumpPress;
            public bool JumpHold;
            public bool Dash;
            public bool Interact;
        }

        [Header("Visualizers")]
        public AvatarSoundVisualizer SoundVisualizer;
        public AvatarAnimationVisualizer AnimationVisualizer;
        public AvatarParticleVisualizer ParticleVisualizer;

        //TODO Extract these variables!
        [Header("Stable Movement")]
        public float MaxStableMoveSpeed = 10f;
        public float StableMovementSharpness = 15;
        public float OrientationSharpness = 10;

        [Header("Air Movement")]
        public float MaxAirMoveSpeed = 10f;
        public float AirAccelerationSpeed = 5f;
        public float AirControlFactor = 1f;
        public float Drag = 0.1f;

        [Header("Jumping")]
        public const float CriticalSpeed = 5f; 
        public bool AllowJumpingWhenSliding;
        public float SingleJumpSpeed = 10f;
        public float DoubleJumpSpeed = 12f;
        public float TrippleJumpSpeed = 15f;
        public float JumpButtonHoldGravityModifier = 0.5f;
        public float JumpDescentGravityModifier = 2.0f;
        public float JumpPreGroundingGraceTime;
        public float JumpPostGroundingGraceTime;
        public float DoubleJumpTimeWindowSize;
        public Vector3 EarthGravity = new Vector3(0, -30, 0);

        //Control state
        private bool jumpTriggeredThisFrame;
        private bool jumpHeldThisFrame;

        //Internal state
        public JumpState CurrentJumpState; //Public for debug
        private JumpType lastJumpType;
        private Vector3 jumpHeading;
        private bool jumpConsumed;
        private float timeSinceLastAbleToJump;
        private float timeSinceJumpRequested = Mathf.Infinity;
        private float timeSinceJumpLanding = Mathf.Infinity;
        private bool landedOnJumpSurfaceLastFrame;

        [Header("Dashing")]
        //Controls
        private bool dashTriggeredThisFrame;

        public DashState CurrentDashState;

        [Header("TemporaryPlanetPrototype")]
        public Transform PlanetTransform;
        private GravityType gravityType;

        [Header("Misc")]
        public List<Collider> IgnoredColliders = new List<Collider>();
        public bool OrientTowardsGravity = true;
        public Vector3 BaseGravity = new Vector3(0, -30f, 0);
        public Transform CameraFollowPoint;
        private Vector3 moveInputVector;
        private Vector3 internalVelocityAdd = Vector3.zero;
        private CharacterState currentCharacterState;

        private int playerLayer;
        private int jumpSurfaceLayer;

        private void Start()
        {
            playerLayer = LayerMask.NameToLayer("Player");
            jumpSurfaceLayer = LayerMask.NameToLayer("JumpSurface");

            // Handle initial state
            TransitionToState(CharacterState.Default);
        }

        /// <summary>
        /// This is called every frame by ExamplePlayer in order to tell the character what its inputs are
        /// </summary>
        public void SetInputs(ref CharacterInputs inputs)
        {
            // Clamp input
            var controllerInput = Vector3.ClampMagnitude(new Vector3(inputs.MoveAxisRight, 0f, inputs.MoveAxisForward), 1f);

            // Calculate camera direction and rotation on the character plane
            var cameraPlanarDirection = Vector3.ProjectOnPlane(inputs.CameraRotation * Vector3.forward, Motor.CharacterUp).normalized;
            if (cameraPlanarDirection.sqrMagnitude == 0f)
            {
                cameraPlanarDirection = Vector3.ProjectOnPlane(inputs.CameraRotation * Vector3.up, Motor.CharacterUp).normalized;
            }
            var cameraPlanarRotation = Quaternion.LookRotation(cameraPlanarDirection, Motor.CharacterUp);

            switch (currentCharacterState)
            {
                case CharacterState.Default:
                {
                    // Move and look inputs
                    moveInputVector = cameraPlanarRotation * controllerInput;

                    // Jumping input
                    if (inputs.JumpPress)
                    {
                        timeSinceJumpRequested = 0f;
                        jumpTriggeredThisFrame = true;
                    }

                    jumpHeldThisFrame = inputs.JumpHold;

                    if (inputs.Interact)
                    {
                        gravityType = gravityType == GravityType.World ? GravityType.Object : GravityType.World;
                    }

                    break;
                }
            }
        }

        /// <summary>
        /// (Called by KinematicCharacterMotor during its update cycle)
        /// This is called before the character begins its movement update
        /// </summary>
        public override void BeforeCharacterUpdate(float deltaTime)
        {
            if (gravityType == GravityType.World || !PlanetTransform)
            {
                BaseGravity = EarthGravity;
            }
            else
            {
                BaseGravity = (PlanetTransform.position - Motor.InitialSimulationPosition).normalized * EarthGravity.magnitude;
            }
        }

        /// <summary>
        /// (Called by KinematicCharacterMotor during its update cycle)
        /// This is where you tell your character what its rotation should be right now. 
        /// This is the ONLY place where you should set the character's rotation
        /// </summary>
        public override void UpdateRotation(ref Quaternion currentRotation, float deltaTime)
        {
            switch (currentCharacterState)
            {
                case CharacterState.Default:
                {
                    if (CurrentJumpState != JumpState.Ascent && CurrentJumpState != JumpState.Descent)
                    {
                        if (moveInputVector != Vector3.zero && OrientationSharpness > 0f)
                        {
                            // Smoothly interpolate from current to target look direction
                            var smoothedLookInputDirection = Vector3.Slerp(Motor.CharacterForward, moveInputVector, 1 - Mathf.Exp(-OrientationSharpness * deltaTime)).normalized;

                            // Set the current rotation (which will be used by the KinematicCharacterMotor)
                            currentRotation = Quaternion.LookRotation(smoothedLookInputDirection, Motor.CharacterUp);
                        }
                    }

                    if (OrientTowardsGravity)
                    {
                        // Rotate from current up to invert gravity
                        currentRotation = Quaternion.FromToRotation((currentRotation * Vector3.up), -BaseGravity) * currentRotation;
                    }

                    if (CurrentJumpState == JumpState.JumpStartedLastFrame)
                    {
                        if (jumpHeading != Vector3.zero)
                        {
                            currentRotation = Quaternion.LookRotation(Vector3.ProjectOnPlane(jumpHeading, Motor.CharacterUp), Motor.CharacterUp);
                        }
                    }
                    break;
                }
            }
        }

        /// <summary>
        /// (Called by KinematicCharacterMotor during its update cycle)
        /// This is where you tell your character what its velocity should be right now. 
        /// This is the ONLY place where you can set the character's velocity
        /// </summary>
        public override void UpdateVelocity(ref Vector3 currentVelocity, float deltaTime)
        {
            switch (currentCharacterState)
            {
                case CharacterState.Default:
                {
                    // Ground movement
                    if (Motor.GroundingStatus.IsStableOnGround)
                    {
                        ApplyGroundMovement(ref currentVelocity, deltaTime);
                    }
                    // Air movement
                    else
                    {
                        ApplyAirMovement(ref currentVelocity, deltaTime);
                        ApplyGravityMovement(ref currentVelocity, deltaTime);
                        // Drag
                        currentVelocity *= 1f / (1f + Drag * deltaTime);
                    }

                    // Handle jump state;
                    timeSinceJumpRequested += deltaTime;
                    if (CurrentJumpState == JumpState.JustLanded)
                    {
                        timeSinceJumpLanding += deltaTime;
                    }
                    if (timeSinceJumpLanding > DoubleJumpTimeWindowSize)
                    {
                        CurrentJumpState = JumpState.Grounded;
                        timeSinceJumpLanding = 0;
                    }
                    if (CurrentJumpState == JumpState.Ascent && Vector3.ProjectOnPlane(currentVelocity, Motor.CharacterForward).y < 0)
                    {
                        CurrentJumpState = JumpState.Descent;
                    }

                    if (CurrentJumpState == JumpState.JumpStartedLastFrame)
                    {
                        CurrentJumpState = JumpState.Ascent;
                    }
                    if (jumpTriggeredThisFrame || landedOnJumpSurfaceLastFrame)
                    {
                        // See if we actually are allowed to jump
                        if (!jumpConsumed &&
                            ((AllowJumpingWhenSliding
                                    ? Motor.GroundingStatus.FoundAnyGround
                                    : Motor.GroundingStatus.IsStableOnGround) ||
                                timeSinceLastAbleToJump <= JumpPostGroundingGraceTime))
                        {
                            DoJump(ref currentVelocity);
                            CurrentJumpState = JumpState.JumpStartedLastFrame;
                            jumpTriggeredThisFrame = false;
                            jumpConsumed = true;
                        }
                    }
                    landedOnJumpSurfaceLastFrame = false;

                    // Take into account additive velocity
                    if (internalVelocityAdd.sqrMagnitude > 0f)
                    {
                        currentVelocity += internalVelocityAdd;
                        internalVelocityAdd = Vector3.zero;
                    }

                    //Handle speed related vfx locally
                    var speed = currentVelocity.magnitude;
                    var isUnderCriticalSpeed = speed > 0.2f && speed < CriticalSpeed;
                    ParticleVisualizer.SetParticleState(ParticleEventType.DustTrail, isUnderCriticalSpeed && Motor.GroundingStatus.FoundAnyGround);

                    break;
                }
            }
        }

        private void ApplyGravityMovement(ref Vector3 currentVelocity, float deltaTime)
        {
            var gravity = BaseGravity * deltaTime;
            switch (CurrentJumpState)
            {
                case JumpState.Descent:
                    gravity *= JumpDescentGravityModifier;
                    break;
                case JumpState.Ascent when jumpHeldThisFrame:
                    gravity *= JumpButtonHoldGravityModifier;
                    break;
            }

            currentVelocity += gravity;
        }

        /// <summary>
        /// (Called by KinematicCharacterMotor during its update cycle)
        /// This is called after the character has finished its movement update
        /// </summary>
        public override void AfterCharacterUpdate(float deltaTime)
        {
            switch (currentCharacterState)
            {
                case CharacterState.Default:
                {
                    // Handle jumping pre-ground grace period
                    if (jumpTriggeredThisFrame && timeSinceJumpRequested > JumpPreGroundingGraceTime)
                    {
                        jumpTriggeredThisFrame = false;
                    }

                    //Move to on landed check?
                    if (AllowJumpingWhenSliding ? Motor.GroundingStatus.FoundAnyGround : Motor.GroundingStatus.IsStableOnGround)
                    {
                        jumpConsumed = false;
                        timeSinceLastAbleToJump = 0f;
                    }
                    else
                    {
                        // Keep track of time since we were last able to jump (for grace period)
                        timeSinceLastAbleToJump += deltaTime;
                    }

                    break;
                }
            }
        }

        public override bool IsColliderValidForCollisions(Collider coll)
        {
            if (IgnoredColliders.Count >= 0)
            {
                return true;
            }

            if (IgnoredColliders.Contains(coll))
            {
                return false;
            }
            return true;
        }

        public override void OnGroundHit(Collider hitCollider, Vector3 hitNormal, Vector3 hitPoint, ref HitStabilityReport hitStabilityReport)
        {
            //Nothing Yet
        }

        public override void OnMovementHit(Collider hitCollider, Vector3 hitNormal, Vector3 hitPoint, ref HitStabilityReport hitStabilityReport)
        {
            //Nothing Yet
        }

        public override void ProcessHitStabilityReport(Collider hitCollider, Vector3 hitNormal, Vector3 hitPoint, Vector3 atCharacterPosition, Quaternion atCharacterRotation, ref HitStabilityReport hitStabilityReport)
        {
        }

        public void AddVelocity(Vector3 velocity)
        {
            switch (currentCharacterState)
            {
                case CharacterState.Default:
                {
                    internalVelocityAdd += velocity;
                    break;
                }
            }
        }

        private void ApplyGroundMovement(ref Vector3 currentVelocity, float deltaTime)
        {
            Vector3 effectiveGroundNormal = Motor.GroundingStatus.GroundNormal;
            if (currentVelocity.sqrMagnitude > 0f && Motor.GroundingStatus.SnappingPrevented)
            {
                // Take the normal from where we're coming from
                Vector3 groundPointToCharacter = Motor.TransientPosition - Motor.GroundingStatus.GroundPoint;
                effectiveGroundNormal = Vector3.Dot(currentVelocity, groundPointToCharacter) >= 0f
                    ? Motor.GroundingStatus.OuterGroundNormal
                    : Motor.GroundingStatus.InnerGroundNormal;
            }

            // Reorient velocity on slope
            currentVelocity = Motor.GetDirectionTangentToSurface(currentVelocity, effectiveGroundNormal) *
                currentVelocity.magnitude;

            // Calculate target velocity
            var inputRight = Vector3.Cross(moveInputVector, Motor.CharacterUp);
            var reorientedInput = Vector3.Cross(effectiveGroundNormal, inputRight).normalized * moveInputVector.magnitude;
            var targetMovementVelocity = reorientedInput * MaxStableMoveSpeed;

            // Smooth movement Velocity
            currentVelocity = Vector3.Lerp(currentVelocity, targetMovementVelocity,
                1 - Mathf.Exp(-StableMovementSharpness * deltaTime));
        }

        private void ApplyAirMovement(ref Vector3 currentVelocity, float deltaTime)
        {
            if (moveInputVector.sqrMagnitude > 0f)
            {
                var targetMovementVelocity = moveInputVector * AirControlFactor * MaxAirMoveSpeed;

                // Prevent climbing on un-stable slopes with air movement
                if (Motor.GroundingStatus.FoundAnyGround)
                {
                    var perpenticularObstructionNormal = Vector3
                        .Cross(Vector3.Cross(Motor.CharacterUp, Motor.GroundingStatus.GroundNormal), Motor.CharacterUp)
                        .normalized;
                    targetMovementVelocity = Vector3.ProjectOnPlane(targetMovementVelocity, perpenticularObstructionNormal);
                }

                //Clamp the velocity diff you can achive while in the air. 

                var velocityDiff = Vector3.ProjectOnPlane(targetMovementVelocity - currentVelocity, BaseGravity);
                currentVelocity += velocityDiff * AirAccelerationSpeed * deltaTime;
            }
        }

        private void DoJump(ref Vector3 currentVelocity)
        {
            // Calculate jump direction before ungrounding
            var upDirection = Motor.CharacterUp;
            if (Motor.GroundingStatus.FoundAnyGround && !Motor.GroundingStatus.IsStableOnGround)
            {
                upDirection = Motor.GroundingStatus.GroundNormal;
            }

            // Makes the character skip ground probing/snapping on its next update. 
            Motor.ForceUnground();

            float jumpSpeed;
            JumpType currentJumpType;
            if (landedOnJumpSurfaceLastFrame)
            {
                currentJumpType = JumpType.JumpPad;
            }
            else if (Vector3.Dot(currentVelocity, moveInputVector) < -0.8)
            {
                currentJumpType = JumpType.Backflip;
            }
            else if (CurrentJumpState == JumpState.JustLanded)
            {
                switch (lastJumpType)
                {
                    case JumpType.Single:
                        currentJumpType = JumpType.Double;
                        break;
                    case JumpType.Double:
                        currentJumpType = JumpType.Tripple;
                        break;
                    case JumpType.Tripple:
                        currentJumpType = JumpType.Single;
                        break;
                    case JumpType.Backflip:
                        currentJumpType = JumpType.Double;
                        break;
                    case JumpType.JumpPad:
                        currentJumpType = JumpType.Double;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
            else
            {
                currentJumpType = JumpType.Single;
            }

            Vector3 jumpDirection;
            switch (currentJumpType)
            {
                case JumpType.Single:
                    jumpSpeed = SingleJumpSpeed;
                    PlayNetworkedSoundEvent(SoundEventType.Wa);
                    jumpDirection = (upDirection * 5 + moveInputVector).normalized;
                    jumpHeading = moveInputVector;
                    break;
                case JumpType.Double:
                    jumpSpeed = DoubleJumpSpeed;
                    PlayNetworkedSoundEvent(SoundEventType.Woo);
                    jumpDirection = (upDirection * 8 + moveInputVector).normalized;
                    jumpHeading = moveInputVector;
                    break;
                case JumpType.Tripple:
                    jumpHeading = currentVelocity;
                    jumpSpeed = TrippleJumpSpeed;
                    PlayNetworkedSoundEvent(SoundEventType.Woohoo);
                    PlayNetworkedAnimationEvent(AnimationEventType.Dive);
                    jumpDirection = (upDirection * 0.5f + moveInputVector.normalized).normalized;
                    break;
                case JumpType.Backflip:
                    jumpHeading = currentVelocity;
                    currentVelocity = moveInputVector;
                    jumpDirection = (upDirection * 5 + moveInputVector).normalized;
                    jumpSpeed = DoubleJumpSpeed;
                    PlayNetworkedSoundEvent(SoundEventType.Woo);
                    PlayNetworkedAnimationEvent(AnimationEventType.Backflip);
                    break;
                case JumpType.JumpPad:
                    jumpSpeed = DoubleJumpSpeed * 1.4f;
                    PlayNetworkedSoundEvent(SoundEventType.Hoo);
                    PlayNetworkedAnimationEvent(AnimationEventType.Backflip);
                    jumpDirection = (upDirection * 12 + moveInputVector).normalized;
                    jumpHeading = moveInputVector;
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            currentVelocity += jumpDirection * jumpSpeed - Vector3.Project(currentVelocity, Motor.CharacterUp);
            lastJumpType = currentJumpType;
        }

        public override void PostGroundingUpdate(float deltaTime)
        {
            // Handle landing and leaving ground
            if (Motor.GroundingStatus.IsStableOnGround && !Motor.LastGroundingStatus.IsStableOnGround)
            {
                OnLanded();
            }
            else if (!Motor.GroundingStatus.IsStableOnGround && Motor.LastGroundingStatus.IsStableOnGround)
            {
                //Leaving ground - not used yet!
            }
        }

        private void OnLanded()
        {
            if (Motor.GroundingStatus.IsStableOnGround)
            {
                PlayNetworkedParticleEvent(ParticleEventType.LandingPoof);
            }

            var objectLandedOn = Motor.GroundingStatus.GroundCollider.gameObject;
            if (objectLandedOn.layer == playerLayer || objectLandedOn.layer == jumpSurfaceLayer)
            {
                landedOnJumpSurfaceLastFrame = true;
            }
            
            CurrentJumpState = JumpState.JustLanded;
            timeSinceJumpLanding = 0;
        }

        private void PlayNetworkedSoundEvent(SoundEventType soundEventType)
        {
            eventWriter?.SendSoundEvent(new SoundEvent((uint) soundEventType));
            SoundVisualizer.PlaySoundEvent(soundEventType);
        }

        private void PlayNetworkedAnimationEvent(AnimationEventType animationEventType)
        {
            eventWriter?.SendAnimationEvent(new AnimationEvent((uint) animationEventType));
            AnimationVisualizer.PlayAnimationEvent(animationEventType);
        }

        private void PlayNetworkedParticleEvent(ParticleEventType particleEvent)
        {
            eventWriter?.SendParticleEvent(new ParticleEvent((uint)particleEvent));
            ParticleVisualizer.PlayParticleEvent(particleEvent);
        }

        /// <summary>
        /// Handles movement state transitions and enter/exit callbacks
        /// </summary>
        private void TransitionToState(CharacterState newState)
        {
            CharacterState tmpInitialState = currentCharacterState;
            OnStateExit(tmpInitialState, newState);
            currentCharacterState = newState;
            OnStateEnter(newState, tmpInitialState);
        }

        /// <summary>
        /// Event when entering a state
        /// </summary>
        private void OnStateEnter(CharacterState state, CharacterState fromState)
        {
            switch (state)
            {
                case CharacterState.Default:
                {
                    break;
                }
            }
        }

        /// <summary>
        /// Event when exiting a state
        /// </summary>
        private void OnStateExit(CharacterState state, CharacterState toState)
        {
            switch (state)
            {
                case CharacterState.Default:
                {
                    break;
                }
            }
        }
    }
}