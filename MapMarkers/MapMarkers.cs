﻿using SRML;
using HarmonyLib;
using UnityEngine;
using SRML.Console;
using SRML.SR;
using SRML.SR.SaveSystem;
using System.Collections.Generic;
using MonomiPark.SlimeRancher.DataModel;
using System.Threading.Tasks;
using System.Threading;
using UnityEngine.UI;
using System.Reflection;

namespace MapMarkers
{
    /// <summary>
    /// Class <c>MapMarkers</c> is the mod entry point and is responsible of initializing variables and attaching event handlers.
    /// </summary>
    class MapMarkers : ModEntryPoint
    {
        /// <summary>
        /// Flag indicating if all treasure pods should be shown on the map.
        /// </summary>
        public static bool ShowAll = false;
        /// <summary>
        /// Flag indicating if already opened treasure pods should be shown on the map (the sprites will be transparent to distinguish them).
        /// </summary>
        public static bool ShowOpened = true;
        /// <summary>
        /// Flag indicating if all Gordos should be shown on the map.
        /// </summary>
        public static bool ShowGordos = false;

        /// <summary>
        /// List containing <c>GameObject</c> position of all discovered treasure pods.
        /// </summary>
        private static List<Vector3> _discoveredTreasurePods = new List<Vector3>();

        /// <summary>
        /// The asset bundle containing assets needed by the mod.
        /// </summary>
        private static AssetBundle _assetBundle;

        /// <summary>
        /// Dictionary mapping an int that represents the type of treasure pods to the sprite to be displayed on the map.
        /// Valid types are:
        /// <list type="bullet">
        /// <item><term>1</term><description> green treasure pod</description></item>
        /// <item><term>2</term><description> blue treasure pod</description></item>
        /// <item><term>3</term><description> purple treasure pod</description></item>
        /// <item><term>4</term><description> cosmetic treasure pod (DLC)</description></item>
        /// </list>
        /// </summary>
        private static readonly Dictionary<int, Sprite> PodSprites = new Dictionary<int, Sprite>();
        public static readonly Dictionary<int, Sprite> OpenPodSprites = new Dictionary<int, Sprite>();

        /// <summary>
        /// Flag used to prevent calling every frame methods to check the treasure pod that the player is looking at.
        /// </summary>
        private static bool _onCooldown = false;


        // Called before GameContext.Awake
        // You want to register new things and enum values here, as well as do all your harmony patching
        public override void PreLoad()
        {
            // Apply all Harmony patches
            HarmonyInstance.PatchAll();

            // Register commands to toggle the flags
            Console.RegisterCommand(new ShowAllCommand());
            Console.RegisterCommand(new ShowOpenedCommand());
            Console.RegisterCommand(new ResetDiscoveredCommand());
            Console.RegisterCommand(new ShowAllGordosCommand());

            // Load mod asset bundle
            _assetBundle = AssetBundle.LoadFromStream(Assembly.GetExecutingAssembly().GetManifestResourceStream(typeof(MapMarkers), "Resources.mapmarkers.assets"));

            // Attach event handler to SRML custom world data load
            SaveRegistry.RegisterWorldDataLoadDelegate((compoundDataPiece) => {
                // If the custom world data has previously set flags, use them
                if (compoundDataPiece.HasPiece("showAllTreasuresOnMap"))
                {
                    ShowAll = compoundDataPiece.GetValue<bool>("showAllTreasuresOnMap");
                }
                else ShowAll = false;
                if (compoundDataPiece.HasPiece("showOpenedTreasuresOnMap"))
                {
                    ShowOpened = compoundDataPiece.GetValue<bool>("showOpenedTreasuresOnMap");
                }
                else ShowOpened = true;
                if (compoundDataPiece.HasPiece("showAllGordosOnMap"))
                {
                    ShowGordos = compoundDataPiece.GetValue<bool>("showAllGordosOnMap");
                }
                else ShowGordos = false;

                _discoveredTreasurePods.Clear();
                // If the custom world data has discovered treasure pods locations saved, load them
                if (compoundDataPiece.HasPiece("discoveredTreasurePods"))
                {
                    _discoveredTreasurePods = new List<Vector3>(compoundDataPiece.GetValue<Vector3[]>("discoveredTreasurePods"));
                }
            });

            // Attach event handler to SRML custom world data save
            SaveRegistry.RegisterWorldDataSaveDelegate((compoundDataPiece) => {
                // Save the current status of flags
                compoundDataPiece.SetValue("showAllTreasuresOnMap", ShowAll);
                compoundDataPiece.SetValue("showOpenedTreasuresOnMap", ShowOpened);
                compoundDataPiece.SetValue("showAllGordosOnMap", ShowGordos);

                // Save the discovered treasure pods locations
                compoundDataPiece.SetValue("discoveredTreasurePods", _discoveredTreasurePods.ToArray());
            });

            // Attach event handler to save game loaded
            SRCallbacks.OnSaveGameLoaded += (scenecontext) =>
            {
                for (int i = 1; i < 5; i++)
                {
                    // Load treasure pod textures from asset bundle and create sprites
                    Texture2D podTexture = _assetBundle.LoadAsset<Texture2D>("treasurepod" + i + ".png");
                    PodSprites[i] = Sprite.Create(podTexture, new Rect(0, 0, 512, 512), new Vector2(0, 0));

                    Texture2D openPodTexture = _assetBundle.LoadAsset<Texture2D>("treasurepod" + i + "_open.png");
                    OpenPodSprites[i] = Sprite.Create(openPodTexture, new Rect(0, 0, 512, 512), new Vector2(0, 0));
                }

                // Create treasure pod map marker prefabs for every type of treasure pod
                GameObject pod1MarkerPrefabObj = CreatePodMarkerPrefab(1);
                GameObject pod2MarkerPrefabObj = CreatePodMarkerPrefab(2);
                GameObject pod3MarkerPrefabObj = CreatePodMarkerPrefab(3);
                GameObject pod4MarkerPrefabObj = CreatePodMarkerPrefab(4);

                // Load MapMarker component for each prefab into a dictionary
                Dictionary<int, MapMarker> podMarkerPrefabs = new Dictionary<int, MapMarker>()
                {
                    {1, pod1MarkerPrefabObj.GetComponent<MapMarker>()},
                    {2, pod2MarkerPrefabObj.GetComponent<MapMarker>()},
                    {3, pod3MarkerPrefabObj.GetComponent<MapMarker>()},
                    {4, pod4MarkerPrefabObj.GetComponent<MapMarker>()},
                };

                // Iterate over all existing treasure pods
                Dictionary<string, TreasurePodModel> podModels = SceneContext.Instance.GameModel.AllPods();
                foreach (KeyValuePair<string, TreasurePodModel> entry in podModels)
                {
                    // Use Harmony Traverse to access treasure pod GameObject instance
                    GameObject gameObject = Traverse.Create(entry.Value).Field("gameObj").GetValue() as GameObject;
                    int type = GetTreasurePodType(gameObject);
                    if(type == -1)
                    {
                        // If a treasure pod of unknow type is found, log to console
                        Console.LogError(entry.Key + " treasure pod has unkown type");
                        continue;
                    }

                    // Add a PodDisplayOnMap component to treasure pod GameObject, using the correct prefab to instantiate the map marker
                    PodDisplayOnMap.AddPodDisplayOnMapComponent(gameObject, podMarkerPrefabs[type]);

                    if(gameObject.GetComponent<TreasurePod>().CurrState == TreasurePod.State.OPEN
                        && !_discoveredTreasurePods.Contains(gameObject.transform.position))
                    {
                        // If loading the mod for the first time in a save that has already opened treasure pods, add them to the discovered list
                        _discoveredTreasurePods.Add(gameObject.transform.position);
                    }
                }
            };
        }


        // Called before GameContext.Start
        // Used for registering things that require a loaded gamecontext
        public override void Load() {}

        // Called after all mods Load's have been called
        // Used for editing existing assets in the game, not a registry step
        public override void PostLoad() {}

        /// <summary>
        /// Creates a new <c>GameObject</c> that will be the pod map marker prefab for this type.
        /// </summary>
        /// <param name="type">an int representing the type of treasure pods <see cref="podTextures"/></param>
        /// <returns>a GameObject to be used as a prefab to instantiate other pod markers of same type</returns>
        private GameObject CreatePodMarkerPrefab(int type)
        {
            // Get an already existing map marker prefab GameObject
            // Using the Gordo one, since it is easy to get
            GameObject gordo = GameContext.Instance.LookupDirector.GetGordo(Identifiable.Id.GOLD_GORDO);
            DisplayOnMap displayOnMap = gordo.GetComponent<GordoDisplayOnMap>();
            GameObject markerPrefabBaseObj = displayOnMap.markerPrefab.gameObject;

            // Clone the map marker prefab GameObject
            GameObject clone = GameObject.Instantiate(markerPrefabBaseObj);
            clone.name = "TreasurePod" + type + "Marker";

            // Assing the correct sprite for the given treasure pod type
            clone.GetComponent<Image>().sprite = PodSprites[type];
            return clone;
        }

        /// <summary>
        /// <para>Adds a treasure pod to the discovered list, if it isn't already present.</para>
        /// Note that this method is effective only every 5 seconds to preserve performance, 
        /// since it needs to be called from an Update method every frame.
        /// </summary>
        /// <param name="treasurePod">the TreasurePod to check</param>
        public static void AddDiscoveredTreasurePod(TreasurePod treasurePod)
        {
            // Since this method will be called every frame from an Update method,
            // use a short cooldown to prevent making too many unnecessary searches in discoveredTreasurePods
            if (_onCooldown) return;

            _onCooldown = true;
            Task.Factory.StartNew(() =>
            {
                // 5 seconds cooldown, it could be even longer since there are no treasure pods that are very close to each other
                Thread.Sleep(5000);

                _onCooldown = false;
            });

            if (!IsTreasurePodDiscovered(treasurePod))
            {
                // If this is a newly found treasure pod, add it to the discovered list
                _discoveredTreasurePods.Add(treasurePod.gameObject.transform.position);
                SECTR_AudioSystem.Play(treasurePod.spawnObjCue, treasurePod.gameObject.transform.position, false);
            }
        }

        /// <summary>
        /// Checks if the <c>TreasurePod</c> has already been discovered by the player.
        /// </summary>
        /// <param name="treasurePod">the TreasurePod to check</param>
        /// <returns><c>true</c> if the treasure pod has already been discovered, <c>false</c> otherwise</returns>
        public static bool IsTreasurePodDiscovered(TreasurePod treasurePod)
        {
            return _discoveredTreasurePods.Contains(treasurePod.gameObject.transform.position);
        }

        /// <summary>
        /// Gets the type of the <c>TreasurePod</c>.
        /// </summary>
        /// <param name="obj">the treasure pod GameObject</param>
        /// <returns>an int representing the type of treasure pod <see cref="podTextures"/></returns>
        public static int GetTreasurePodType(GameObject obj)
        {
            string name = obj.name;
            if (name.Contains("Rank1")) return 1;
            if (name.Contains("Rank2")) return 2;
            if (name.Contains("Rank3")) return 3;
            if (name.Contains("Cosmetic")) return 4;
            return -1;
        }

        /// <summary>
        /// Resets all discovered treasure pods, leaving only opened treasure pod in the discovered list.
        /// </summary>
        public static void ResetDiscoveredTreasurePods()
        {
            _discoveredTreasurePods.Clear();

            Dictionary<string, TreasurePodModel> podModels = SceneContext.Instance.GameModel.AllPods();
            foreach (KeyValuePair<string, TreasurePodModel> entry in podModels)
            {
                // Use Harmony Traverse to access treasure pod GameObject instance
                GameObject gameObject = Traverse.Create(entry.Value).Field("gameObj").GetValue() as GameObject;

                if (gameObject.GetComponent<TreasurePod>().CurrState == TreasurePod.State.OPEN
                    && !_discoveredTreasurePods.Contains(gameObject.transform.position))
                {
                    // Add back all open treasure pods to the discovered list
                    _discoveredTreasurePods.Add(gameObject.transform.position);
                }
            }
        }

        /// <summary>
        /// If map is open, force <c>MapUI</c> to refresh, updating all map markers.
        /// </summary>
        public static void ForceRefreshMap()
        {
            MapUI mapUI = SRSingleton<Map>.Instance.mapUI;
            if (mapUI.gameObject.activeSelf) Traverse.Create(mapUI).Method("RefreshMap").GetValue();
        }
    }

    /// <summary>
    /// Component to be attached to a treasure pod that should be displayed on the map.
    /// </summary>
    class PodDisplayOnMap: DisplayOnMap
    {
        // Reference to the TreasurePod component
        public TreasurePod treasurePod;
        // Flag to check when TreasurePod.State changes from LOCKED to OPEN
        private bool _opened = false;

        /// <summary>
        /// Adds a new <c>PodDisplayOnMap</c> component to the given <c>GameObject</c>, initializing all required fields.
        /// </summary>
        /// <param name="obj">the GameObject to attach the component to</param>
        /// <param name="podMarkerPrefab">the MapMarker prefab that will be used to create the MapMarker for this GameObject</param>
        public static void AddPodDisplayOnMapComponent(GameObject obj, MapMarker podMarkerPrefab)
        {
            // Set the GameObject inactive, so that the component's Awake() method is not called before necessary fields are set
            obj.SetActive(false);
            PodDisplayOnMap podDisplayOnMap = obj.AddComponent<PodDisplayOnMap>() as PodDisplayOnMap;
            podDisplayOnMap.markerPrefab = podMarkerPrefab;
            podDisplayOnMap.HideInFog = true;
            podDisplayOnMap.treasurePod = obj.GetComponent<TreasurePod>();
            obj.SetActive(true);
        }

        public override void Refresh()
        {
            if(!_opened)
            {
                // If treasure pod was locked and now is open, change the sprite of the MapMarker
                bool nowOpened = treasurePod.CurrState == TreasurePod.State.OPEN;
                if(nowOpened) SetOpened();
            }
        }

        public override bool ShowOnMap()
        {
            if(base.ShowOnMap())
            {
                if (MapMarkers.ShowAll) return true;
                else if (MapMarkers.ShowOpened) return MapMarkers.IsTreasurePodDiscovered(treasurePod);
                else {
                    return MapMarkers.IsTreasurePodDiscovered(treasurePod) && treasurePod.CurrState == TreasurePod.State.LOCKED;
                }
            }
            return false;
        }

        /// <summary>
        /// Sets the treasure pod as opened, making the sprite of the MapMarker semi transparent.
        /// </summary>
        private void SetOpened()
        {
            _opened = true;
            Image markerImg = base.GetMarker().gameObject.GetComponent<Image>();
            Color temp = markerImg.color;
            temp.a = 0.7f;
            markerImg.color = temp;
            markerImg.sprite = MapMarkers.OpenPodSprites[MapMarkers.GetTreasurePodType(base.gameObject)];
        }
    }

    /// <summary>
    /// Patch for <c>UIDetector.Update()</c> method that is responsible of checking when the player discovers new treasure pods.
    /// </summary>
    [HarmonyPatch(typeof(UIDetector))]
    [HarmonyPatch("Update")]
    class Patch01
    {
        static void Postfix(UIDetector instance)
        {
            // Use a RaycastHit from the main camera to check if the player is looking from close enough at a treasure pod
            Vector3 pos = new Vector3((float)Screen.width * 0.5f, (float)Screen.height * 0.5f, 0f);
            RaycastHit raycastHit;
            Camera mainCamera = Traverse.Create(instance).Field("mainCamera").GetValue() as Camera;
            Physics.Raycast(mainCamera.ScreenPointToRay(pos), out raycastHit, instance.interactDistance);
            TreasurePod treasurePod = null;
            if (raycastHit.collider != null)
            {
                GameObject gameObject = raycastHit.collider.gameObject;
                treasurePod = gameObject.GetComponent<TreasurePod>();
            }

            if (treasurePod != null)
            {
                MapMarkers.AddDiscoveredTreasurePod(treasurePod);
            }
        }
    }

    [HarmonyPatch(typeof(GordoDisplayOnMap))]
    [HarmonyPatch("ShowOnMap")]
    class Patch02
    {
        static void Postfix(GordoDisplayOnMap instance, ref bool result)
        {
            if(MapMarkers.ShowGordos)
            {
                CellDirector parentCellDirector = Traverse.Create(instance).Method("GetParentCellDirector").GetValue<CellDirector>();
                result = (!(parentCellDirector != null) || !parentCellDirector.notShownOnMap);
            }
        }
    }

    class ShowAllCommand : ConsoleCommand
    {
        public override string ID => "showalltreasures";

        public override string Usage => "showalltreasures";

        public override string Description => "Toggles if all treasure pods should be shown on the map";

        public override bool Execute(string[] args)
        {
            MapMarkers.ShowAll = !MapMarkers.ShowAll;

            MapMarkers.ForceRefreshMap();

            if (MapMarkers.ShowAll) Console.Log("[MapMarkers]: Now showing all treasure pods on the map!");
            else Console.Log("[MapMarkers]: No longer showing all treasure pods on the map!");
            return true;
        }
    }

    class ShowOpenedCommand : ConsoleCommand
    {
        public override string ID => "showopenedtreasures";

        public override string Usage => "showopenedtreasures";

        public override string Description => "Toggles if already opened treasure pods should be shown on the map";

        public override bool Execute(string[] args)
        {
            MapMarkers.ShowOpened = !MapMarkers.ShowOpened;

            MapMarkers.ForceRefreshMap();

            if (MapMarkers.ShowOpened) Console.Log("[MapMarkers]: Now showing opened treasure pods on the map!");
            else Console.Log("[MapMarkers]: No longer showing opened treasure pods on the map!");
            return true;
        }
    }

    class ResetDiscoveredCommand : ConsoleCommand
    {
        public override string ID => "resetdiscoveredtreasures";

        public override string Usage => "resetdiscoveredtreasures";

        public override string Description => "Resets all locked treasure pods to undiscovered (no longer shows them on map)";

        public override bool Execute(string[] args)
        {
            MapMarkers.ResetDiscoveredTreasurePods();

            MapMarkers.ForceRefreshMap();

            Console.Log("[MapMarkers]: Reset successful!");
            return true;
        }
    }

    class ShowAllGordosCommand : ConsoleCommand
    {
        public override string ID => "showallgordos";

        public override string Usage => "showallgordos";

        public override string Description => "Toggles if all gordos should be shown on the map";

        public override bool Execute(string[] args)
        {
            MapMarkers.ShowGordos = !MapMarkers.ShowGordos;

            MapMarkers.ForceRefreshMap();

            if (MapMarkers.ShowGordos) Console.Log("[MapMarkers]: Now showing all gordos on the map!");
            else Console.Log("[MapMarkers]: No longer showing all gordos on the map!");
            return true;
        }
    }
}
