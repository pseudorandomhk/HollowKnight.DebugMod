﻿using System;
using System.Collections;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using DebugMod.Hitbox;
using GlobalEnums;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using Modding;
using On;
using UnityEngine;
using UnityEngine.SceneManagement;
using USceneManager = UnityEngine.SceneManagement.SceneManager;

namespace DebugMod
{
    /// <summary>
    /// Handles struct SaveStateData and individual SaveState operations
    /// </summary>
     internal class SaveState

     {
        // Some mods (ItemChanger) check type to detect vanilla scene loads.
        private class DebugModSaveStateSceneLoadInfo : GameManager.SceneLoadInfo { }

        //used to stop double loads/saves
        public static bool loadingSavestate { get; private set; }
        internal static bool isPanthState = false;

        [Serializable]
        public class SaveStateData
        {
            public string saveStateIdentifier;
            public string saveScene;
            public PlayerData savedPd;
            public object lockArea;
            public SceneData savedSd;
            public Vector3 savePos;
            public FieldInfo cameraLockArea;
            public string filePath;
            public bool isKinematized;
            public string[] loadedScenes;
            public string[] loadedSceneActiveScenes;
            //Special Case Variables
            public int specialIndex;
            public string roomSpecificOptions;


            internal SaveStateData() { }

            internal SaveStateData(SaveStateData _data)
            {
                saveStateIdentifier = _data.saveStateIdentifier;
                saveScene = _data.saveScene;

                //Special Case Variables
                specialIndex = _data.specialIndex;
                cameraLockArea = _data.cameraLockArea;
                savedPd = _data.savedPd;
                savedSd = _data.savedSd;
                savePos = _data.savePos;
                lockArea = _data.lockArea;
                isKinematized = _data.isKinematized;
                roomSpecificOptions = _data.roomSpecificOptions;

                if (_data.loadedScenes is not null)
                {
                    loadedScenes = new string[_data.loadedScenes.Length];
                    Array.Copy(_data.loadedScenes, loadedScenes, _data.loadedScenes.Length);
                }
                else
                {
                    loadedScenes = new[] { saveScene };
                }

                loadedSceneActiveScenes = new string[loadedScenes.Length];
                if (_data.loadedSceneActiveScenes is not null)
                {
                    Array.Copy(_data.loadedSceneActiveScenes, loadedSceneActiveScenes, loadedSceneActiveScenes.Length);
                }
                else
                {
                    for (int i = 0; i < loadedScenes.Length; i++)
                    {
                        loadedSceneActiveScenes[i] = loadedScenes[i];
                    }
                }

            }
        }

        [SerializeField]
        public SaveStateData data;

        internal SaveState()
        {

            data = new SaveStateData();
        }

        #region saving

        public void SaveTempState()
        {
            //save level state before savestates so levers and dead enemies persist properly
            GameManager.instance.SaveLevelState();
            data.saveScene = GameManager.instance.GetSceneNameString();
            data.saveStateIdentifier = $"(tmp)_{data.saveScene}-{DateTime.Now.ToString("H:mm_d-MMM")}";
            //implementation so room specifics can be automatically saved
            try
            {
                var roomSpecificData = RoomSpecific.SaveRoomSpecific(data.saveScene);
                data.roomSpecificOptions = roomSpecificData.value ?? 0.ToString();
                data.specialIndex = roomSpecificData.index;
            }
            catch (Exception e)
            {
                DebugMod.instance.LogError(e);
            }
            data.savedPd = JsonUtility.FromJson<PlayerData>(JsonUtility.ToJson(PlayerData.instance));
            data.savedSd = JsonUtility.FromJson<SceneData>(JsonUtility.ToJson(SceneData.instance));
            data.savePos = HeroController.instance.gameObject.transform.position;
            data.cameraLockArea = (data.cameraLockArea ?? typeof(CameraController).GetField("currentLockArea", BindingFlags.Instance | BindingFlags.NonPublic));
            data.lockArea = data.cameraLockArea.GetValue(GameManager.instance.cameraCtrl);
            data.isKinematized = HeroController.instance.GetComponent<Rigidbody2D>().isKinematic;
            var scenes = SceneWatcher.LoadedScenes;
            data.loadedScenes = scenes.Select(s => s.name).ToArray();
            data.loadedSceneActiveScenes = scenes.Select(s => s.activeSceneWhenLoaded).ToArray();
            Console.AddLine("Saved temp state");
        }

        public void NewSaveStateToFile(int paramSlot)
        {
            SaveTempState();
            SaveStateToFile(paramSlot);
        }

        public void SaveStateToFile(int paramSlot)
        {
            try
            {
                if (data.saveStateIdentifier.StartsWith("(tmp)_"))
                {
                    data.saveStateIdentifier = data.saveStateIdentifier.Substring(6);
                }
                else if (String.IsNullOrEmpty(data.saveStateIdentifier))
                {
                    throw new Exception("No temp save state set");
                }

                string saveStateFile = Path.Combine(SaveStateManager.path, $"savestate{paramSlot}.json");
                File.WriteAllText(saveStateFile,
                    JsonUtility.ToJson(data, prettyPrint: true)
                );
            }
            catch (Exception ex)
            {
                DebugMod.instance.LogDebug(ex.Message);
                throw ex;
            }
        }
        #endregion

        #region loading

        //loadDuped is used by external mods
        public void LoadTempState(bool loadDuped = false)
        {
            if (!PlayerDeathWatcher.playerDead && 
                !HeroController.instance.cState.transitioning && 
                HeroController.instance.transform.parent == null && // checks if in elevator/conveyor
                !loadingSavestate)
            {
                GameManager.instance.StartCoroutine(LoadStateCoro(loadDuped));
            }
            else if (DebugMod.overrideLoadLockout)
            {
                Console.AddLine("Attempting Savestate Load Override");
                GameManager.instance.StartCoroutine(LoadStateCoro(loadDuped));
            }
            else
            {
                Console.AddLine("SaveStates cannot be loaded when dead, transitioning, or on elevators");
            }
        }

        //loadDuped is used by external mods
        public void NewLoadStateFromFile(bool loadDuped = false)
        {
            LoadStateFromFile(SaveStateManager.currentStateSlot);
            LoadTempState(loadDuped);
        }

        public void LoadStateFromFile(int paramSlot)
        {
            try
            {
                data.filePath = Path.Combine(SaveStateManager.path, $"savestate{paramSlot}.json");

                if (File.Exists(data.filePath))
                {
                    //DebugMod.instance.Log("checked filepath: " + data.filePath);
                    SaveStateData tmpData = JsonUtility.FromJson<SaveStateData>(File.ReadAllText(data.filePath));
                    try
                    {
                        data = new SaveStateData(tmpData);

                        DebugMod.instance.Log("Load SaveState ready: " + data.saveStateIdentifier);
                    }
                    catch (Exception ex)
                    {
                        DebugMod.instance.LogError("Error applying save state data: " + ex);
                    }
                }
            }
            catch (Exception ex)
            {
                DebugMod.instance.LogDebug(ex);
                throw;
            }
        }

        //loadDuped is used by external mods
        private IEnumerator LoadStateCoro(bool loadDuped)
        {
            //var used to prevent saves/loads, double save/loads softlock in menderbug, double load, black screen, etc
            loadingSavestate = true;
            bool stateondeath = DebugMod.stateOnDeath;
            DebugMod.stateOnDeath = false;

            //prevents silly things from happening
            Time.timeScale = 0;

            //timer for loading savestates
            System.Diagnostics.Stopwatch loadingStateTimer = new System.Diagnostics.Stopwatch();
            loadingStateTimer.Start();

            //setup panth stuff since it wants to be loaded one room ahead of time
            if (PanthSaveState.panthSequences.Contains(data.roomSpecificOptions) && (data.saveScene != "GG_Atrium") &&  (data.saveScene != "GG_Atrium_Roof"))
            {
                PanthSaveState.LoadPanthScene(data.roomSpecificOptions, data.specialIndex);
            }

            //called here because this needs to be done here
            if (DebugMod.savestateFixes)
            {
                //TODO: Cleaner way to do this? Also get it to actually work
                //prevent hazard respawning
                if (DebugMod.CurrentHazardCoro != null) HeroController.instance.StopCoroutine(DebugMod.CurrentHazardCoro);
                if (DebugMod.CurrentInvulnCoro != null) HeroController.instance.StopCoroutine(DebugMod.CurrentInvulnCoro);
                DebugMod.CurrentHazardCoro = null;
                DebugMod.CurrentInvulnCoro = null;

                //fixes knockback storage
                ReflectionHelper.CallMethod(HeroController.instance, "CancelDamageRecoil");

                //ends hazard respawn animation
                var invPulse = HeroController.instance.GetComponent<InvulnerablePulse>();
                invPulse.stopInvulnerablePulse();
            }

            if (data.savedPd == null || string.IsNullOrEmpty(data.saveScene)) yield break;

            //remove dialogues if exists
            PlayMakerFSM.BroadcastEvent("BOX DOWN DREAM");
            PlayMakerFSM.BroadcastEvent("CONVO CANCEL");

            GameManager.instance.entryGateName = "dreamGate";
            GameManager.instance.startedOnThisScene = true;

            //Menderbug room loads faster (Thanks Magnetic Pizza)
            string dummySceneName =
                UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "Room_Mender_House" ?
                    "Room_Mender_House" :
                    "Room_Sly_Storeroom";

            USceneManager.LoadScene(dummySceneName);

            yield return new WaitUntil(() => USceneManager.GetActiveScene().name == dummySceneName);

            JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(data.savedSd), SceneData.instance);
            GameManager.instance.ResetSemiPersistentItems();

            yield return null;

            JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(data.savedPd), PlayerData.instance);

            SceneWatcher.LoadedSceneInfo[] sceneData = data
                .loadedScenes
                .Zip(data.loadedSceneActiveScenes, (name, gameplay) => new SceneWatcher.LoadedSceneInfo(name, gameplay))
                .ToArray();

            sceneData[0].LoadHook();

            //this kills enemies that were dead on the state, they respawn from previous code
            JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(data.savedSd), SceneData.instance);

            //TODO:Set this more eloquently
            if (isPanthState)
            {
                HeroController.instance.IgnoreInputWithoutReset();
                HeroController.instance.EnterWithoutInput(true);
                GameManager.instance.BeginSceneTransition
                (
                    new DebugModSaveStateSceneLoadInfo
                    {
                        SceneName = data.saveScene,
                        HeroLeaveDirection = GatePosition.unknown,
                        EntryGateName = "door_dreamEnter",
                        EntryDelay = 0f,
                        WaitForSceneTransitionCameraFade = false,
                        Visualization = GameManager.SceneLoadVisualizations.GodsAndGlory,
                        AlwaysUnloadUnusedAssets = true
                    }
                );
            }   
            else
            {

                GameManager.instance.BeginSceneTransition
                (
                    new DebugModSaveStateSceneLoadInfo
                    {
                        SceneName = data.saveScene,
                        HeroLeaveDirection = GatePosition.unknown,
                        EntryGateName = "dreamGate",
                        EntryDelay = 0f,
                        WaitForSceneTransitionCameraFade = false,
                        Visualization = 0,
                        AlwaysUnloadUnusedAssets = true
                    }
                );
            }

            yield return new WaitUntil(() => USceneManager.GetActiveScene().name == data.saveScene);

            GameManager.instance.cameraCtrl.PositionToHero(false);

            ReflectionHelper.SetField(GameManager.instance.cameraCtrl, "isGameplayScene", true);
            ReflectionHelper.CallMethod(GameManager.instance, "UpdateUIStateFromGameState");

            if (loadDuped)
            {
                yield return new WaitUntil(() => GameManager.instance.IsInSceneTransition == false);
                for (int i = 1; i < sceneData.Length; i++)
                {
                    On.GameManager.UpdateSceneName += sceneData[i].UpdateSceneNameOverride;
                    AsyncOperation loadop = USceneManager.LoadSceneAsync(sceneData[i].name, LoadSceneMode.Additive);
                    loadop.allowSceneActivation = true;
                    yield return loadop;
                    On.GameManager.UpdateSceneName -= sceneData[i].UpdateSceneNameOverride;
                    GameManager.instance.RefreshTilemapInfo(sceneData[i].name);
                    GameManager.instance.cameraCtrl.SceneInit();
                }
                GameManager.instance.BeginScene();
            }

            if (data.lockArea != null)
            {
                GameManager.instance.cameraCtrl.LockToArea(data.lockArea as CameraLockArea);
            }

            GameManager.instance.cameraCtrl.FadeSceneIn();

            HeroController.instance.CharmUpdate();

            PlayMakerFSM.BroadcastEvent("CHARM INDICATOR CHECK");    //update twister             
            PlayMakerFSM.BroadcastEvent("UPDATE NAIL DAMAGE");       //update nail

            FieldInfo cameraGameplayScene = typeof(CameraController).GetField("isGameplayScene", BindingFlags.Instance | BindingFlags.NonPublic);

            cameraGameplayScene.SetValue(GameManager.instance.cameraCtrl, true);

            yield return null;

            HeroController.instance.gameObject.transform.position = data.savePos;
            HeroController.instance.transitionState = HeroTransitionState.WAITING_TO_TRANSITION;
            HeroController.instance.GetComponent<Rigidbody2D>().isKinematic = data.isKinematized;

            loadingStateTimer.Stop();
            loadingSavestate = false;

            if (loadDuped && DebugMod.settings.ShowHitBoxes > 0)
            {
                int cs = DebugMod.settings.ShowHitBoxes;
                DebugMod.settings.ShowHitBoxes = 0;
                yield return new WaitUntil(() => HitboxViewer.State == 0);
                DebugMod.settings.ShowHitBoxes = cs;
            }

            if (!isPanthState) ReflectionHelper.CallMethod(HeroController.instance, "FinishedEnteringScene", true, false);

            if (data.roomSpecificOptions != "0" && data.roomSpecificOptions != null)
            {
                Console.AddLine("Performing Room Specific Option " + data.roomSpecificOptions);
                RoomSpecific.DoRoomSpecific(data.saveScene, data.roomSpecificOptions, data.specialIndex);
            }
            //removes things like bench storage no clip float etc
            if (DebugMod.settings.SaveStateGlitchFixes) SaveStateGlitchFixes();

            //pause fixes from homothety
            if (GameManager.instance.isPaused)
            {
                GameManager.instance.FadeSceneIn();
                GameManager.instance.isPaused = false;
                GameCameras.instance.ResumeCameraShake();
                if (HeroController.SilentInstance != null)
                {
                    HeroController.instance.UnPause();
                }
                MenuButtonList.ClearAllLastSelected();
                TimeController.GenericTimeScale = 1f;
            }

            TimeSpan loadingStateTime = loadingStateTimer.Elapsed;

            HUDFixes();
            
            if (isPanthState) yield return PanthSaveState.SetupPanthTransition();

            //set timescale back
            Time.timeScale = DebugMod.CurrentTimeScale;
            DebugMod.stateOnDeath = stateondeath;

            Console.AddLine("Loaded savestate in " + loadingStateTime.ToString(@"ss\.fff") + "s");

        }
        
        //these are toggleable, as they will prevent glitches from persisting
        private void SaveStateGlitchFixes()
        {
            var rb2d = HeroController.instance.GetComponent<Rigidbody2D>();
            PlayMakerFSM wakeFSM = HeroController.instance.gameObject.LocateMyFSM("Dream Return");
            PlayMakerFSM spellFSM = HeroController.instance.gameObject.LocateMyFSM("Spell Control");

            //White screen fixes
            wakeFSM.SetState("Idle");

            //float
            HeroController.instance.AffectedByGravity(true);
            rb2d.gravityScale = 0.79f;
            spellFSM.SetState("Inactive");
                
            //invuln
            HeroController.instance.gameObject.LocateMyFSM("Roar Lock").FsmVariables.FindFsmBool("No Roar").Value = false;
            HeroController.instance.cState.invulnerable = false;

            //no clip
            rb2d.isKinematic = false;

            //bench storage
            GameManager.instance.SetPlayerDataBool(nameof(PlayerData.atBench), false);

            if (HeroController.SilentInstance != null)
            {
                if (HeroController.instance.cState.onConveyor || HeroController.instance.cState.onConveyorV || HeroController.instance.cState.inConveyorZone)
                {
                    HeroController.instance.GetComponent<ConveyorMovementHero>()?.StopConveyorMove();
                    HeroController.instance.cState.inConveyorZone = false;
                    HeroController.instance.cState.onConveyor = false;
                    HeroController.instance.cState.onConveyorV = false;
                }

                HeroController.instance.cState.nearBench = false;
            }
        }

        //Moving all HUD related code to here for clarity
        private void HUDFixes()
        {

            GameCameras.instance.hudCanvas.gameObject.SetActive(true);

            HeroController.instance.geoCounter.geoTextMesh.text = data.savedPd.geo.ToString();

            bool isInfiniteHp = DebugMod.infiniteHP;
            DebugMod.infiniteHP = false;
            PlayerData.instance.hasXunFlower = false;
            PlayerData.instance.health = data.savedPd.health;
            HeroController.instance.TakeHealth(1);
            HeroController.instance.AddHealth(1);
            PlayerData.instance.hasXunFlower = data.savedPd.hasXunFlower;
            DebugMod.infiniteHP = isInfiniteHp;

            int healthBlue = data.savedPd.healthBlue;
            for (int i = 0; i < healthBlue; i++)
            {
                EventRegister.SendEvent("ADD BLUE HEALTH");
            }

            //should fix hp
            //the "Idle" mesh never gets disabled when Charm Indicator runs
            //running the animation quickly would work but this does too and i understand it
            int maxHP = GameManager.instance.playerData.maxHealth;
            for (int i = maxHP; i > 0; i--)
            {
                GameObject health = GameObject.Find("Health " + i.ToString());
                PlayMakerFSM fsm = health.LocateMyFSM("health_display");
                Transform idle = health.transform.Find("Idle");
                //This is the "HP Full" mesh, which covers the empty mesh
                idle.gameObject.GetComponent<MeshRenderer>().enabled = false;
                //This is the "HP Empty" mesh
                health.GetComponent<MeshRenderer>().enabled = true;
                //This turns back on the "HP Full" mesh if we should
                fsm.SetState("Check if Full");
            }

            if (PlayerData.instance.MPReserveMax < 33) GameObject.Find("Vessel 1").LocateMyFSM("vessel_orb").SetState("Init");
            else GameObject.Find("Vessel 1").LocateMyFSM("vessel_orb").SetState("Up Check");
            if (PlayerData.instance.MPReserveMax < 66) GameObject.Find("Vessel 2").LocateMyFSM("vessel_orb").SetState("Init");
            else GameObject.Find("Vessel 2").LocateMyFSM("vessel_orb").SetState("Up Check");
            if (PlayerData.instance.MPReserveMax < 99) GameObject.Find("Vessel 3").LocateMyFSM("vessel_orb").SetState("Init");
            else GameObject.Find("Vessel 3").LocateMyFSM("vessel_orb").SetState("Up Check");
            if (PlayerData.instance.MPReserveMax < 132) GameObject.Find("Vessel 4").LocateMyFSM("vessel_orb").SetState("Init");
            else GameObject.Find("Vessel 4").LocateMyFSM("vessel_orb").SetState("Up Check");

            HeroController.instance.TakeMP(1);
            HeroController.instance.AddMPChargeSpa(1);

            PlayMakerFSM.BroadcastEvent("MP DRAIN");
            PlayMakerFSM.BroadcastEvent("MP LOSE");
            PlayMakerFSM.BroadcastEvent("MP RESERVE DOWN");

        }
        #endregion

        #region helper functionality

        public bool IsSet()
        {
            bool isSet = !String.IsNullOrEmpty(data.saveStateIdentifier);
            return isSet;
        }

        public string GetSaveStateID()
        {
            return data.saveStateIdentifier;
        }

        public string[] GetSaveStateInfo()
        {
            return new string[]
            {
                data.saveStateIdentifier,
                data.saveScene
            };
        }
        public SaveState.SaveStateData DeepCopy()
        {
            return new SaveState.SaveStateData(this.data);
        }
        #endregion
    }
}