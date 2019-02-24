using Gameschema.Untrusted;
using Improbable;
using Improbable.Gdk.Core;
using Improbable.Gdk.PlayerLifecycle;
using Improbable.Gdk.TransformSynchronization;
using Playground;
using UnityEngine;

namespace FastPlatformer.Config.EntityTemplates
{
    public static class PlayerTemplate
    {
        public static EntityTemplate CreatePlayerEntityTemplate(string workerId, Vector3f position)
        {
            var clientAttribute = $"workerId:{workerId}";

            //Core
            var template = new EntityTemplate();
            template.AddComponent(new Position.Snapshot(), clientAttribute);
            template.AddComponent(new Metadata.Snapshot { EntityType = "PlatformerCharacter" }, WorkerUtils.UnityGameLogic);
            TransformSynchronizationHelper.AddTransformSynchronizationComponents(template, clientAttribute, position.ToUnityVector(), Vector3.zero);
            PlayerLifecycleHelper.AddPlayerLifecycleComponents(template, workerId, clientAttribute, WorkerUtils.UnityGameLogic);
            template.SetReadAccess(WorkerUtils.UnityClient, WorkerUtils.UnityGameLogic, WorkerUtils.AndroidClient, WorkerUtils.iOSClient);
            template.SetComponentWriteAccess(EntityAcl.ComponentId, WorkerUtils.UnityGameLogic);

            //Addons
            template.AddComponent(new PlayerInput.Snapshot(), clientAttribute);
            template.AddComponent(new PlayerVisualizerEvents.Snapshot(), clientAttribute);

            return template;
        }
    }
}