// Stub of VRChat's VRC.Dynamics assembly.
//
// A .warudo / VRChat avatar bundle carries VRCPhysBone(Collider) components as MonoBehaviours
// whose serialized data is unreadable in any host that lacks the VRChat SDK - the scripts load
// as dead "missing script" placeholders and their tuning is lost. This assembly is a data-only
// re-declaration of those classes with the EXACT assembly name, type names and serialized field
// names/types (extracted from VRChat Avatars SDK 3.10.4), so Unity's AssetBundle deserializer
// binds the bundle's components to these classes and repopulates every field. Nothing here
// simulates - the Warudo Importer reads these revived fields and builds native DynamicBone
// components from them, exactly the way Warudo converts VRCPhysBone -> Dynamic Bone at load.
//
// This file is compiled TWICE, into two separate assemblies whose names must match what the
// bundle references: VRC.Dynamics.dll (the *Base classes + enums) and
// VRC.SDK3.Dynamics.PhysBone.dll (the concrete VRCPhysBone/VRCPhysBoneCollider). See the two
// .rsp files. The split mirrors the real SDK: the derived classes declare no fields of their own.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace VRC.Dynamics
{
    public enum DynamicsUsageFlags { Nothing = 0, Avatar = 1, World = 2, Everything = -1 }
    public enum DynamicsUsage { Unassigned = 0, Avatar = 1, World = 2 }

    // Unity serializes a plain struct by its public fields, so the layout must match for the
    // enclosing component to deserialize past it.
    [Serializable]
    public struct PermissionFilter
    {
        public bool allowSelf;
        public bool allowOthers;
        public DynamicsUsageFlags contentTypes;
    }

    public class DynamicsComponent : MonoBehaviour { }

    public class VRCPhysBoneColliderBase : DynamicsComponent
    {
        public enum ShapeType { Sphere = 0, Capsule = 1, Plane = 2 }

        public Transform rootTransform;
        public ShapeType shapeType;
        public bool insideBounds;
        public float radius;
        public float height;
        public Vector3 position;
        public Quaternion rotation = Quaternion.identity;
        public bool bonesAsSpheres;
        public DynamicsUsageFlags globalCollisionFlags;
    }

    public class VRCPhysBoneBase : DynamicsComponent
    {
        public enum Version { Version_1_0 = 0, Version_1_1 = 1 }
        public enum IntegrationType { Simplified = 0, Advanced = 1 }
        public enum MultiChildType { Ignore = 0, First = 1, Average = 2 }
        public enum ImmobileType { AllMotion = 0, World = 1 }
        public enum LimitType { None = 0, Angle = 1, Hinge = 2, Polar = 3 }
        public enum AdvancedBool { False = 0, True = 1, Other = 2 }

        // Editor foldout flags are serialized in the real component; keeping them lets the
        // TypeTree line up field-for-field even though we never read them.
        public bool foldout_transforms, foldout_forces, foldout_collision, foldout_stretchsquish;
        public bool foldout_limits, foldout_grabpose, foldout_options, foldout_gizmos;

        public Version version;
        public IntegrationType integrationType;

        public Transform rootTransform;
        public List<Transform> ignoreTransforms;
        public bool ignoreOtherPhysBones;
        public Vector3 endpointPosition;
        public MultiChildType multiChildType;

        public float pull;
        public AnimationCurve pullCurve;
        public float spring;
        public AnimationCurve springCurve;
        public float stiffness;
        public AnimationCurve stiffnessCurve;
        public float gravity;
        public AnimationCurve gravityCurve;
        public float gravityFalloff;
        public AnimationCurve gravityFalloffCurve;

        public ImmobileType immobileType;
        public float immobile;
        public AnimationCurve immobileCurve;

        public AdvancedBool allowCollision;
        public PermissionFilter collisionFilter;

        public float radius;
        public AnimationCurve radiusCurve;
        public List<VRCPhysBoneColliderBase> colliders;

        public LimitType limitType;
        public float maxAngleX;
        public AnimationCurve maxAngleXCurve;
        public float maxAngleZ;
        public AnimationCurve maxAngleZCurve;
        public Vector3 limitRotation;
        public AnimationCurve limitRotationXCurve;
        public AnimationCurve limitRotationYCurve;
        public AnimationCurve limitRotationZCurve;

        public AdvancedBool allowGrabbing;
        public PermissionFilter grabFilter;
        public AdvancedBool allowPosing;
        public PermissionFilter poseFilter;
        public bool snapToHand;
        public float grabMovement;
        public float maxStretch;
        public AnimationCurve maxStretchCurve;
        public float maxSquish;
        public AnimationCurve maxSquishCurve;
        public float stretchMotion;
        public AnimationCurve stretchMotionCurve;

        public bool isAnimated;
        public bool resetWhenDisabled;
        public string parameter;
        public bool showGizmos;
        public float boneOpacity;
        public float limitOpacity;
    }
}
