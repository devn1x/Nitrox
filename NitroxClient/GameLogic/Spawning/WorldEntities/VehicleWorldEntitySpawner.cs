using System.Collections;
using NitroxClient.Helpers;
using NitroxClient.MonoBehaviours;
using NitroxClient.MonoBehaviours.Overrides;
using NitroxClient.Unity.Helper;
using NitroxModel.DataStructures.GameLogic.Entities;
using NitroxModel.DataStructures.Util;
using NitroxModel.Helper;
using NitroxModel_Subnautica.DataStructures;
using UnityEngine;
using static NitroxClient.GameLogic.Helper.TransientLocalObjectManager;

namespace NitroxClient.GameLogic.Spawning.WorldEntities;

public class VehicleWorldEntitySpawner : IWorldEntitySpawner
{
    private const float CONSTRUCTION_DURATION_IN_SECONDS = 10f;

    public IEnumerator SpawnAsync(WorldEntity entity, Optional<GameObject> parent, EntityCell cellRoot, TaskResult<Optional<GameObject>> result)
    { 
        VehicleWorldEntity vehicleEntity = (VehicleWorldEntity)entity;

        bool withinConstructorSpawnWindow = (DayNightCycle.main.timePassedAsFloat - vehicleEntity.ConstructionTime) < CONSTRUCTION_DURATION_IN_SECONDS;
        Optional<GameObject> spawnerObj = NitroxEntity.GetObjectFrom(vehicleEntity.SpawnerId);

        if (withinConstructorSpawnWindow && spawnerObj.HasValue)
        {
            Constructor constructor = spawnerObj.Value.GetComponent<Constructor>();

            if (constructor)
            {
                MobileVehicleBay.TransmitLocalSpawns = false;
                yield return SpawnViaConstructor(vehicleEntity, constructor, result);
                MobileVehicleBay.TransmitLocalSpawns = false;
                yield break;
            }
        }

        yield return SpawnInWorld(vehicleEntity, result, parent);            
    }

    private IEnumerator SpawnInWorld(VehicleWorldEntity vehicleEntity, TaskResult<Optional<GameObject>> result, Optional<GameObject> parent)
    {
        TechType techType = vehicleEntity.TechType.ToUnity();
        GameObject gameObject = null;

        if (techType == TechType.Cyclops)
        {
            LightmappedPrefabs.main.RequestScenePrefab("cyclops", (go) => gameObject = go);
            yield return new WaitUntil(() => gameObject != null);
        }
        else
        {
            CoroutineTask<GameObject> techPrefabCoroutine = CraftData.GetPrefabForTechTypeAsync(techType, false);
            yield return techPrefabCoroutine;
            GameObject techPrefab = techPrefabCoroutine.GetResult();
            gameObject = Utils.SpawnPrefabAt(techPrefab, null, vehicleEntity.Transform.Position.ToUnity());
            Validate.NotNull(gameObject, $"{nameof(VehicleWorldEntitySpawner)}: No prefab for tech type: {techType}");
            Vehicle vehicle = gameObject.GetComponent<Vehicle>();

            if(vehicle)
            {
                vehicle.LazyInitialize();
            }
        }

        AddCinematicControllers(gameObject);

        gameObject.transform.position = vehicleEntity.Transform.Position.ToUnity();
        gameObject.transform.rotation = vehicleEntity.Transform.Rotation.ToUnity();
        gameObject.SetActive(true);
        gameObject.SendMessage("StartConstruction", SendMessageOptions.DontRequireReceiver);

        CrafterLogic.NotifyCraftEnd(gameObject, CraftData.GetTechType(gameObject));
        Rigidbody rigidBody = gameObject.RequireComponent<Rigidbody>();
        rigidBody.isKinematic = false;

        yield return Yielders.WaitForEndOfFrame;

        RemoveConstructionAnimations(gameObject);

        yield return Yielders.WaitForEndOfFrame;

        // Sometimes build templates, such as the cyclops, are already tagged with IDs.  Remove any that exist to retag.
        UnityEngine.Component.DestroyImmediate(gameObject.GetComponent<NitroxEntity>());
        NitroxEntity.SetNewId(gameObject, vehicleEntity.Id);

        if (parent.HasValue)
        {
            DockVehicle(gameObject, parent.Value);
        }

        result.Set(gameObject);
    }

    private IEnumerator SpawnViaConstructor(VehicleWorldEntity vehicleEntity, Constructor constructor, TaskResult<Optional<GameObject>> result)
    {
        if (!constructor.deployed)
        {
            constructor.Deploy(true);
        }

        float craftDuration = CONSTRUCTION_DURATION_IN_SECONDS - (DayNightCycle.main.timePassedAsFloat - vehicleEntity.ConstructionTime);

        Crafter crafter = constructor.gameObject.RequireComponentInChildren<Crafter>(true);
        crafter.OnCraftingBegin(vehicleEntity.TechType.ToUnity(), craftDuration);

        Optional<object> opConstructedObject = Get(TransientObjectType.CONSTRUCTOR_INPUT_CRAFTED_GAMEOBJECT);
        Validate.IsTrue(opConstructedObject.HasValue, $"Could not find constructed object {vehicleEntity.Id} from constructor {constructor.gameObject.name}");

        GameObject constructedObject = (GameObject)opConstructedObject.Value;

        // Sometimes build templates, such as the cyclops, are already tagged with IDs.  Remove any that exist to retag.
        UnityEngine.Component.DestroyImmediate(constructedObject.GetComponent<NitroxEntity>());
        NitroxEntity.SetNewId(constructedObject, vehicleEntity.Id);

        result.Set(constructedObject);
        yield break;
    }

    /// <summary>
    ///   For scene objects like cyclops, PlayerCinematicController Start() will not be called to add Cinematic reference.
    /// </summary>
    private void AddCinematicControllers(GameObject gameObject)
    {
        if (gameObject.GetComponent<MultiplayerCinematicReference>())
        {
            return;
        }

        PlayerCinematicController[] controllers = gameObject.GetComponentsInChildren<PlayerCinematicController>();

        if (controllers.Length == 0)
        {
            return;
        }

        MultiplayerCinematicReference reference = gameObject.AddComponent<MultiplayerCinematicReference>();

        foreach (PlayerCinematicController controller in controllers)
        {
            reference.AddController(controller);
        }
    }

    /// <summary>
    ///  When loading in vehicles, they still briefly have their blue crafting animation playing.  Force them to stop.
    /// </summary>
    private void RemoveConstructionAnimations(GameObject gameObject)
    {
        VFXConstructing[] vfxConstructions = gameObject.GetComponentsInChildren<VFXConstructing>();
        
        foreach (VFXConstructing vfxConstructing in vfxConstructions)
        {
            vfxConstructing.EndGracefully();
        }
    }

    private void DockVehicle(GameObject gameObject, GameObject parent)
    {
        Vehicle vehicle = gameObject.GetComponent<Vehicle>();

        if (!vehicle)
        {
            Log.Info($"Could not find vehicle component on docked vehicle {gameObject.name}");
            return;
        }

        VehicleDockingBay dockingBay = parent.GetComponentInChildren<VehicleDockingBay>();

        if (!dockingBay)
        {
            Log.Info($"Could not find VehicleDockingBay component on dock object {parent.name}");
            return;
        }

        dockingBay.DockVehicle(vehicle);        
    }

    public bool SpawnsOwnChildren()
    {
        return false;
    }
}
