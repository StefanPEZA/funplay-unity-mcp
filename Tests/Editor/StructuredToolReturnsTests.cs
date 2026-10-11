// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Funplay.Editor.State;
using Funplay.Editor.Tools;
using Funplay.Editor.Tools.Builtins;
using Funplay.Editor.Tools.Helpers;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Funplay.Editor.Tests
{
    public sealed class StructuredToolReturnsTests
    {
        [Test]
        public void GetComponentProperties_ExpandsArraysAndNestedStructs()
        {
            var go = new GameObject("__FunplayNestedProperties", typeof(LineRenderer));
            try
            {
                var line = go.GetComponent<LineRenderer>();
                line.sharedMaterials = new Material[2];
                var id = ObjectIdHelper.GetSerializableId(line);
                var data = Newtonsoft.Json.Linq.JObject.FromObject(ComponentPropertyFunctions.GetComponentProperties(component_instance_id: id))["data"];
                var properties = (Newtonsoft.Json.Linq.JArray)data["properties"];

                var materials = properties.Single(p => (string)p["Name"] == "m_Materials");
                Assert.AreEqual(Newtonsoft.Json.Linq.JTokenType.Array, materials["Value"].Type, materials.ToString());
                Assert.AreEqual(2, ((Newtonsoft.Json.Linq.JArray)materials["Value"]).Count, materials.ToString());
                Assert.IsNull(materials["DisplayName"], "a DisplayName that is just the nicified name is left out: " + materials);

                var nested = properties.Where(p => (string)p["Type"] == "Generic" && p["Value"].Type == Newtonsoft.Json.Linq.JTokenType.Object).ToList();
                Assert.IsNotEmpty(nested, "a serialized struct reads as {field: value}: " + properties);
                Assert.IsFalse(properties.Any(p => (string)p["Type"] == "Generic" && p["Value"].Type == Newtonsoft.Json.Linq.JTokenType.String && (string)p["Value"] == "Generic"), properties.ToString());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void GetCameraProperties_ReturnsStructuredValues()
        {
            var cameraObject = new GameObject(
                "__FunplayStructuredCamera",
                typeof(Camera));
            var targetTexture = new RenderTexture(64, 32, 16)
            {
                name = "__FunplayStructuredCameraTarget"
            };

            try
            {
                var camera = cameraObject.GetComponent<Camera>();
                camera.orthographic = true;
                camera.orthographicSize = 7.5f;
                camera.nearClipPlane = 0.25f;
                camera.farClipPlane = 750f;
                camera.backgroundColor = new Color32(26, 51, 77, 102);
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.cullingMask = (1 << 0) | (1 << 5);
                camera.rect = new Rect(0.1f, 0.2f, 0.7f, 0.6f);
                camera.targetDisplay = 2;
                camera.targetTexture = targetTexture;
                cameraObject.transform.position = new Vector3(1f, 2f, 3f);
                cameraObject.transform.eulerAngles = new Vector3(10f, 20f, 30f);

                var result = CameraFunctions.GetCameraProperties(cameraObject.name);
                AssertSuccess(result);
                var data = GetProperty<object>(result, "data");

                Assert.AreEqual(
                    ObjectIdHelper.GetSerializableId(cameraObject),
                    GetProperty<string>(data, "instanceId"));
                Assert.AreEqual(cameraObject.name, GetProperty<string>(data, "name"));
                Assert.IsTrue(GetProperty<bool>(data, "orthographic"));
                Assert.AreEqual(7.5f, GetProperty<float>(data, "orthographicSize"));
                Assert.AreEqual(0.25f, GetProperty<float>(data, "nearClipPlane"));
                Assert.AreEqual(750f, GetProperty<float>(data, "farClipPlane"));
                Assert.AreEqual("SolidColor", GetProperty<string>(data, "clearFlags"));
                Assert.AreEqual(targetTexture.name, GetProperty<string>(data, "targetTexture"));

                var background = GetProperty<object>(data, "backgroundColor");
                Assert.AreEqual("#1A334D66", GetProperty<string>(background, "hex"));

                var maskLayers = ((IEnumerable)GetProperty<object>(
                    data,
                    "cullingMaskLayers")).Cast<string>().ToArray();
                CollectionAssert.Contains(maskLayers, "Default");
                CollectionAssert.Contains(maskLayers, "UI");

                var position = GetProperty<object>(data, "position");
                Assert.AreEqual(1f, GetProperty<float>(position, "x"));
                Assert.AreEqual(2f, GetProperty<float>(position, "y"));
                Assert.AreEqual(3f, GetProperty<float>(position, "z"));
            }
            finally
            {
                var camera = cameraObject.GetComponent<Camera>();
                if (camera != null)
                    camera.targetTexture = null;
                targetTexture.Release();
                UnityEngine.Object.DestroyImmediate(targetTexture);
                UnityEngine.Object.DestroyImmediate(cameraObject);
            }
        }

        [Test]
        public void GetCameraProperties_MissingCameraReturnsStructuredError()
        {
            var result = CameraFunctions.GetCameraProperties(
                "__FunplayMissingCamera_" + Guid.NewGuid().ToString("N"));

            AssertError(result, "CAMERA_NOT_FOUND");
        }

        [Test]
        public void GetReloadRecoveryStatus_UsesStableShapeAndConsumeSemantics()
        {
            var previous = DomainReloadHandler.GetLastRecoveryInfo(consume: true);
            try
            {
                var empty = CompilationFunctions.GetReloadRecoveryStatus();
                AssertSuccess(empty);
                var emptyData = GetProperty<object>(empty, "data");
                Assert.IsFalse(GetProperty<bool>(emptyData, "recorded"));
                Assert.AreEqual("none", GetProperty<string>(emptyData, "status"));
                Assert.IsNull(GetProperty<string>(emptyData, "tool"));
                Assert.IsNull(GetProperty<string>(emptyData, "timestamp"));
                Assert.IsNull(GetProperty<string>(emptyData, "summary"));

                DomainReloadHandler.StoreRecoveryInfo(
                    "execute_code",
                    "Success",
                    "Compilation finished after reload.");

                var recorded = CompilationFunctions.GetReloadRecoveryStatus();
                AssertSuccess(recorded);
                var recordedData = GetProperty<object>(recorded, "data");
                Assert.IsTrue(GetProperty<bool>(recordedData, "recorded"));
                Assert.AreEqual("Success", GetProperty<string>(recordedData, "status"));
                Assert.AreEqual("execute_code", GetProperty<string>(recordedData, "tool"));
                Assert.AreEqual(
                    "Compilation finished after reload.",
                    GetProperty<string>(recordedData, "summary"));
                Assert.IsTrue(DateTime.TryParse(
                    GetProperty<string>(recordedData, "timestamp"),
                    out _));

                AssertSuccess(CompilationFunctions.GetReloadRecoveryStatus(consume: true));
                var afterConsume = CompilationFunctions.GetReloadRecoveryStatus();
                Assert.IsFalse(GetProperty<bool>(
                    GetProperty<object>(afterConsume, "data"),
                    "recorded"));
            }
            finally
            {
                DomainReloadHandler.GetLastRecoveryInfo(consume: true);
                if (previous != null)
                {
                    DomainReloadHandler.StoreRecoveryInfo(
                        previous.ToolName,
                        previous.Status,
                        previous.Summary);
                }
            }
        }

        [Test]
        public void SimulateMouseClick_OutsidePlayModeReturnsStructuredErrorAndIsNotReadOnly()
        {
            Assert.IsFalse(EditorApplication.isPlaying);

            var result = InputInteractionFunctions.SimulateMouseClick(10, 20);
            AssertError(result, "PLAY_MODE_REQUIRED");

            var method = typeof(InputInteractionFunctions).GetMethod(
                nameof(InputInteractionFunctions.SimulateMouseClick),
                BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(method);
            Assert.IsNull(method.GetCustomAttribute<ReadOnlyToolAttribute>());
        }

        [Test]
        public void DispatchUiClick_ButtonInvokesOnce()
        {
            var buttonObject = new GameObject(
                "__FunplaySingleClickButton",
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image),
                typeof(Button));
            GameObject eventSystemObject = null;

            try
            {
                var invocationCount = 0;
                buttonObject.GetComponent<Button>().onClick.AddListener(() => invocationCount++);
                var eventSystem = GetOrCreateEventSystem(out eventSystemObject);

                var outcome = DispatchUiClick(buttonObject, eventSystem);

                Assert.IsTrue(outcome.Hit);
                Assert.AreEqual("ui-eventsystem", outcome.Strategy);
                Assert.AreEqual(buttonObject.name, outcome.Target.Name);
                Assert.AreEqual(1, invocationCount);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(buttonObject);
                if (eventSystemObject != null)
                    UnityEngine.Object.DestroyImmediate(eventSystemObject);
            }
        }

        [Test]
        public void DispatchUiClick_ReportsParentClickReceiver()
        {
            var buttonObject = new GameObject(
                "__FunplayParentClickReceiver",
                typeof(RectTransform),
                typeof(Button));
            var raycastObject = new GameObject("__FunplayRaycastChild", typeof(RectTransform));
            raycastObject.transform.SetParent(buttonObject.transform, false);
            GameObject eventSystemObject = null;

            try
            {
                var invocationCount = 0;
                buttonObject.GetComponent<Button>().onClick.AddListener(() => invocationCount++);
                var eventSystem = GetOrCreateEventSystem(out eventSystemObject);

                var outcome = DispatchUiClick(raycastObject, eventSystem);

                Assert.IsTrue(outcome.Hit);
                Assert.AreEqual(buttonObject.name, outcome.Target.Name);
                Assert.AreEqual(
                    ObjectIdHelper.GetSerializableId(buttonObject),
                    outcome.Target.InstanceId);
                Assert.AreEqual(1, invocationCount);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(buttonObject);
                if (eventSystemObject != null)
                    UnityEngine.Object.DestroyImmediate(eventSystemObject);
            }
        }

        [Test]
        public void DispatchUiClick_BlockerWithoutHandlerDoesNotReportHit()
        {
            var blocker = new GameObject("__FunplayClickBlocker");
            GameObject eventSystemObject = null;

            try
            {
                var eventSystem = GetOrCreateEventSystem(out eventSystemObject);
                var outcome = DispatchUiClick(blocker, eventSystem);

                Assert.IsFalse(outcome.Hit);
                Assert.IsTrue(outcome.BlocksFurtherDispatch);
                Assert.AreEqual(blocker.name, outcome.BlockedBy.Name);
                Assert.IsNull(outcome.Target);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(blocker);
                if (eventSystemObject != null)
                    UnityEngine.Object.DestroyImmediate(eventSystemObject);
            }
        }

        [Test]
        public void DispatchUiClick_SelfDestroyingReceiverUsesPreDispatchSnapshot()
        {
            var target = new GameObject("__FunplaySelfDestroyingClickReceiver");
            target.AddComponent<DestroyOnPointerClickForTest>();
            GameObject eventSystemObject = null;

            try
            {
                var expectedName = target.name;
                var expectedId = ObjectIdHelper.GetSerializableId(target);
                var eventSystem = GetOrCreateEventSystem(out eventSystemObject);

                var outcome = DispatchUiClick(target, eventSystem);

                Assert.IsTrue(outcome.Hit);
                Assert.AreEqual(expectedName, outcome.Target.Name);
                Assert.AreEqual(expectedId, outcome.Target.InstanceId);
                Assert.IsTrue(target == null, "The test receiver should destroy itself.");
            }
            finally
            {
                if (target != null)
                    UnityEngine.Object.DestroyImmediate(target);
                if (eventSystemObject != null)
                    UnityEngine.Object.DestroyImmediate(eventSystemObject);
            }
        }

        [Test]
        public void DispatchUiClick_TargetDestroyedOnPointerDownDoesNotReportClick()
        {
            var target = new GameObject("__FunplayPointerDownDestroyReceiver");
            target.AddComponent<DestroyOnPointerDownForTest>();
            GameObject eventSystemObject = null;

            try
            {
                var expectedName = target.name;
                var eventSystem = GetOrCreateEventSystem(out eventSystemObject);

                var outcome = DispatchUiClick(target, eventSystem);

                Assert.IsFalse(outcome.Hit);
                Assert.IsTrue(outcome.BlocksFurtherDispatch);
                Assert.AreEqual(expectedName, outcome.BlockedBy.Name);
                Assert.IsTrue(target == null, "The pointer-down receiver should destroy itself.");
            }
            finally
            {
                if (target != null)
                    UnityEngine.Object.DestroyImmediate(target);
                if (eventSystemObject != null)
                    UnityEngine.Object.DestroyImmediate(eventSystemObject);
            }
        }

        private static InputInteractionFunctions.ClickOutcome DispatchUiClick(
            GameObject target,
            EventSystem eventSystem)
        {
            var pointerData = new PointerEventData(eventSystem)
            {
                position = Vector2.zero,
                pressPosition = Vector2.zero,
                button = PointerEventData.InputButton.Left
            };
            var attempts = new List<InputInteractionFunctions.ClickOutcome>();
            var outcome = InputInteractionFunctions.DispatchUiClick(
                target,
                default,
                pointerData,
                attempts);

            Assert.AreEqual(1, attempts.Count);
            Assert.AreSame(outcome, attempts[0]);
            return outcome;
        }

        private static EventSystem GetOrCreateEventSystem(out GameObject ownedObject)
        {
            if (EventSystem.current != null)
            {
                ownedObject = null;
                return EventSystem.current;
            }

            ownedObject = new GameObject(
                "__FunplayStructuredReturnEventSystem",
                typeof(EventSystem));
            return ownedObject.GetComponent<EventSystem>();
        }

        private static void AssertSuccess(object result)
        {
            Assert.IsTrue(GetProperty<bool>(result, "success"), Describe(result));
        }

        private static void AssertError(object result, string expectedCode)
        {
            Assert.IsFalse(GetProperty<bool>(result, "success"), Describe(result));
            Assert.AreEqual(expectedCode, GetProperty<string>(result, "code"));
            Assert.AreEqual(expectedCode, GetProperty<string>(result, "error"));
        }

        private static T GetProperty<T>(object target, string name)
        {
            Assert.IsNotNull(target);
            var property = target.GetType().GetProperty(name);
            Assert.IsNotNull(property, $"Missing property '{name}' on {target.GetType().FullName}.");
            return (T)property.GetValue(target);
        }

        private static string Describe(object value)
        {
            return value == null ? "null" : value.ToString();
        }
    }

    internal sealed class DestroyOnPointerClickForTest : MonoBehaviour, IPointerClickHandler
    {
        public void OnPointerClick(PointerEventData eventData)
        {
            DestroyImmediate(gameObject);
        }
    }

    internal sealed class DestroyOnPointerDownForTest :
        MonoBehaviour,
        IPointerDownHandler,
        IPointerClickHandler
    {
        public void OnPointerDown(PointerEventData eventData)
        {
            DestroyImmediate(gameObject);
        }

        public void OnPointerClick(PointerEventData eventData)
        {
        }
    }
}
