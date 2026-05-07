import * as THREE from "https://cdn.jsdelivr.net/npm/three@0.164.1/build/three.module.js";

const statusText = document.getElementById("statusText");
const hostMessageText = document.getElementById("hostMessageText");
const pageLog = document.getElementById("pageLog");
const sendToHostButton = document.getElementById("sendToHostButton");
const resetMappingButton = document.getElementById("resetMappingButton");
const resetViewportButton = document.getElementById("resetViewportButton");
const resetPoseButton = document.getElementById("resetPoseButton");
const clearPageLogButton = document.getElementById("clearPageLogButton");
const rollSlider = document.getElementById("rollSlider");
const pitchSlider = document.getElementById("pitchSlider");
const yawSlider = document.getElementById("yawSlider");
const orientationPresetSelect = document.getElementById("orientationPreset");
const mapPitchSelect = document.getElementById("mapPitchSelect");
const mapYawSelect = document.getElementById("mapYawSelect");
const mapRollSelect = document.getElementById("mapRollSelect");
const viewYawSlider = document.getElementById("viewYawSlider");
const viewPitchSlider = document.getElementById("viewPitchSlider");
const viewDistanceSlider = document.getElementById("viewDistanceSlider");
const rollValue = document.getElementById("rollValue");
const pitchValue = document.getElementById("pitchValue");
const yawValue = document.getElementById("yawValue");
const viewYawValue = document.getElementById("viewYawValue");
const viewPitchValue = document.getElementById("viewPitchValue");
const viewDistanceValue = document.getElementById("viewDistanceValue");
const viewMotionScaleSlider = document.getElementById("viewMotionScaleSlider");
const viewTrailStepSlider = document.getElementById("viewTrailStepSlider");
const viewMotionScaleValue = document.getElementById("viewMotionScaleValue");
const viewTrailStepValue = document.getElementById("viewTrailStepValue");
const devicePresetSelect = document.getElementById("devicePresetSelect");
const moduleOffsetXSlider = document.getElementById("moduleOffsetXSlider");
const moduleOffsetYSlider = document.getElementById("moduleOffsetYSlider");
const moduleOffsetZSlider = document.getElementById("moduleOffsetZSlider");
const moduleYawSlider = document.getElementById("moduleYawSlider");
const modulePitchSlider = document.getElementById("modulePitchSlider");
const moduleRollSlider = document.getElementById("moduleRollSlider");
const moduleOffsetXValue = document.getElementById("moduleOffsetXValue");
const moduleOffsetYValue = document.getElementById("moduleOffsetYValue");
const moduleOffsetZValue = document.getElementById("moduleOffsetZValue");
const moduleYawValue = document.getElementById("moduleYawValue");
const modulePitchValue = document.getElementById("modulePitchValue");
const moduleRollValue = document.getElementById("moduleRollValue");
const showTrailCheckbox = document.getElementById("showTrailCheckbox");
const resetLayoutButton = document.getElementById("resetLayoutButton");
const poseBadge = document.getElementById("poseBadge");
const viewport = document.getElementById("viewport");
const sceneCanvas = document.getElementById("sceneCanvas");

const pose = {
  roll: 0,
  pitch: 0,
  yaw: 0,
  label: "neutral",
};

const pageLogLines = ["[page] Ready."];
const maxPageLogLines = 140;
let lastPoseLogMs = 0;

const targetRotation = {
  x: 0,
  y: 0,
  z: 0,
};

let orientationPreset = "calibrated-v1";

const orientationAxisMap = {
  pitch: "pitch",
  yaw: "yaw",
  roll: "-roll",
};

const targetPosition = {
  x: 0,
  y: 0,
  z: 0,
};

const trailPoints = [];
const maxTrailPoints = 600;

const visualization = {
  motionScale: 8.0,
  minTrailStep: 0.018,
};

const moduleLayout = {
  preset: "none",
  offsetX: 0.0,
  offsetY: 0.18,
  offsetZ: 0.0,
  yawDeg: 0,
  pitchDeg: 0,
  rollDeg: 0,
  showTrail: true,
};

const renderer = new THREE.WebGLRenderer({ canvas: sceneCanvas, antialias: true, alpha: true });
renderer.setPixelRatio(window.devicePixelRatio || 1);

const scene = new THREE.Scene();
const camera = new THREE.PerspectiveCamera(48, 1, 0.1, 120);

const viewportCamera = {
  yawDeg: 34,
  pitchDeg: 22,
  distance: 6.2,
  targetY: 0.22,
};

scene.add(new THREE.AmbientLight(0xffffff, 1.4));

const mainLight = new THREE.DirectionalLight(0xb9ffe8, 2.1);
mainLight.position.set(4, 5, 3);
scene.add(mainLight);

const fillLight = new THREE.DirectionalLight(0x8bb6ff, 1.2);
fillLight.position.set(-3, 1, -4);
scene.add(fillLight);

const deviceShellGroup = new THREE.Group();
scene.add(deviceShellGroup);

const grid = new THREE.GridHelper(18, 18, 0x2e7c67, 0x1b3d36);
grid.position.y = -1.8;
scene.add(grid);

const axes = new THREE.AxesHelper(3.0);
axes.position.y = -0.15;
scene.add(axes);

const cubeGroup = new THREE.Group();
scene.add(cubeGroup);

const cube = new THREE.Mesh(
  new THREE.BoxGeometry(0.18, 0.72, 1.16),
  new THREE.MeshStandardMaterial({
    color: 0x4cd7aa,
    roughness: 0.22,
    metalness: 0.08,
    transparent: true,
    opacity: 0.68,
  })
);
cubeGroup.add(cube);

const edgeLines = new THREE.LineSegments(
  new THREE.EdgesGeometry(new THREE.BoxGeometry(0.18, 0.72, 1.16)),
  new THREE.LineBasicMaterial({ color: 0xe8fff9 })
);
cubeGroup.add(edgeLines);

const nose = new THREE.Mesh(
  new THREE.ConeGeometry(0.16, 0.4, 24),
  new THREE.MeshStandardMaterial({ color: 0xffd166, roughness: 0.3, metalness: 0.05 })
);
nose.rotation.x = Math.PI / 2;
nose.position.z = 0.66;
cubeGroup.add(nose);

const currentMarker = new THREE.Mesh(
  new THREE.SphereGeometry(0.085, 20, 20),
  new THREE.MeshStandardMaterial({ color: 0xff8f66, emissive: 0x5a2a18, roughness: 0.3 })
);
scene.add(currentMarker);

const trailGeometry = new THREE.BufferGeometry().setFromPoints([new THREE.Vector3(0, 0, 0)]);
const trailLine = new THREE.Line(
  trailGeometry,
  new THREE.LineBasicMaterial({ color: 0xffd166, transparent: true, opacity: 0.96 })
);
scene.add(trailLine);

const mountAnchor = new THREE.Mesh(
  new THREE.SphereGeometry(0.045, 16, 16),
  new THREE.MeshStandardMaterial({ color: 0x6cf8d3, emissive: 0x1d5a49, roughness: 0.35 })
);
scene.add(mountAnchor);

const mountAxes = new THREE.AxesHelper(0.35);
scene.add(mountAxes);

function applyDevicePreset() {
  deviceShellGroup.clear();

  const shellMaterial = new THREE.MeshStandardMaterial({
    color: 0x5b6b78,
    roughness: 0.42,
    metalness: 0.12,
    transparent: true,
    opacity: 0.45,
  });

  if (moduleLayout.preset === "glasses-temple") {
    const temple = new THREE.Mesh(new THREE.BoxGeometry(2.8, 0.12, 0.18), shellMaterial);
    temple.position.set(0.0, 0.18, 0.0);
    deviceShellGroup.add(temple);

    const rim = new THREE.Mesh(new THREE.TorusGeometry(0.56, 0.03, 16, 48), shellMaterial);
    rim.rotation.y = Math.PI / 2;
    rim.position.set(-1.05, 0.12, 0.0);
    deviceShellGroup.add(rim);
  } else if (moduleLayout.preset === "headband-front") {
    const band = new THREE.Mesh(new THREE.TorusGeometry(0.95, 0.055, 18, 72, Math.PI), shellMaterial);
    band.rotation.z = Math.PI;
    band.position.set(0.0, 0.34, 0.0);
    deviceShellGroup.add(band);

    const frontPlate = new THREE.Mesh(new THREE.BoxGeometry(0.9, 0.2, 0.16), shellMaterial);
    frontPlate.position.set(0.0, 0.1, 0.0);
    deviceShellGroup.add(frontPlate);
  } else if (moduleLayout.preset === "ear-hook") {
    const hook = new THREE.Mesh(new THREE.TorusGeometry(0.38, 0.045, 16, 64, Math.PI * 1.4), shellMaterial);
    hook.rotation.x = Math.PI / 2;
    hook.rotation.z = -0.6;
    hook.position.set(0.06, 0.16, 0.0);
    deviceShellGroup.add(hook);

    const arm = new THREE.Mesh(new THREE.BoxGeometry(0.68, 0.1, 0.15), shellMaterial);
    arm.position.set(-0.25, 0.2, 0.0);
    deviceShellGroup.add(arm);
  }
}

function applyLayoutFromControls() {
  moduleLayout.preset = devicePresetSelect.value;
  moduleLayout.offsetX = Number(moduleOffsetXSlider.value);
  moduleLayout.offsetY = Number(moduleOffsetYSlider.value);
  moduleLayout.offsetZ = Number(moduleOffsetZSlider.value);
  moduleLayout.yawDeg = Number(moduleYawSlider.value);
  moduleLayout.pitchDeg = Number(modulePitchSlider.value);
  moduleLayout.rollDeg = Number(moduleRollSlider.value);
  moduleLayout.showTrail = showTrailCheckbox.value === "on";

  moduleOffsetXValue.textContent = moduleLayout.offsetX.toFixed(2);
  moduleOffsetYValue.textContent = moduleLayout.offsetY.toFixed(2);
  moduleOffsetZValue.textContent = moduleLayout.offsetZ.toFixed(2);
  moduleYawValue.textContent = String(moduleLayout.yawDeg);
  modulePitchValue.textContent = String(moduleLayout.pitchDeg);
  moduleRollValue.textContent = String(moduleLayout.rollDeg);

  trailLine.visible = moduleLayout.showTrail;
  applyDevicePreset();
}

function appendPageLog(message) {
  const wasNearBottom = pageLog.scrollTop + pageLog.clientHeight >= pageLog.scrollHeight - 18;
  const time = new Date().toLocaleTimeString();
  pageLogLines.push(`[${time}] ${message}`);
  while (pageLogLines.length > maxPageLogLines) {
    pageLogLines.shift();
  }

  pageLog.textContent = pageLogLines.join("\n");
  if (wasNearBottom) {
    pageLog.scrollTop = pageLog.scrollHeight;
  }
}

function resizeScene() {
  const width = viewport.clientWidth;
  const height = viewport.clientHeight;

  if (width === 0 || height === 0) {
    return;
  }

  renderer.setSize(width, height, false);
  camera.aspect = width / height;
  camera.updateProjectionMatrix();
}

function applyViewportCamera() {
  const yawRad = THREE.MathUtils.degToRad(viewportCamera.yawDeg);
  const pitchRad = THREE.MathUtils.degToRad(viewportCamera.pitchDeg);
  const planar = viewportCamera.distance * Math.cos(pitchRad);

  camera.position.x = planar * Math.sin(yawRad);
  camera.position.y = viewportCamera.targetY + viewportCamera.distance * Math.sin(pitchRad);
  camera.position.z = planar * Math.cos(yawRad);
  camera.lookAt(0, viewportCamera.targetY, 0);
}

function applyViewportFromControls() {
  viewportCamera.yawDeg = Number(viewYawSlider.value);
  viewportCamera.pitchDeg = Number(viewPitchSlider.value);
  viewportCamera.distance = Number(viewDistanceSlider.value);
  visualization.motionScale = Number(viewMotionScaleSlider.value);
  visualization.minTrailStep = Number(viewTrailStepSlider.value);

  viewYawValue.textContent = String(viewportCamera.yawDeg);
  viewPitchValue.textContent = String(viewportCamera.pitchDeg);
  viewDistanceValue.textContent = viewportCamera.distance.toFixed(1);
  viewMotionScaleValue.textContent = visualization.motionScale.toFixed(1);
  viewTrailStepValue.textContent = visualization.minTrailStep.toFixed(3);

  applyViewportCamera();
}

function setPose(nextPose) {
  pose.roll = Number(nextPose.roll ?? 0);
  pose.pitch = Number(nextPose.pitch ?? 0);
  pose.yaw = Number(nextPose.yaw ?? 0);
  pose.label = nextPose.label ?? "custom";

  const mapped = mapOrientationByPreset(pose.roll, pose.pitch, pose.yaw);
  targetRotation.x = THREE.MathUtils.degToRad(readAxisToken(orientationAxisMap.pitch, mapped));
  targetRotation.y = THREE.MathUtils.degToRad(readAxisToken(orientationAxisMap.yaw, mapped));
  targetRotation.z = THREE.MathUtils.degToRad(readAxisToken(orientationAxisMap.roll, mapped));

  rollSlider.value = String(pose.roll);
  pitchSlider.value = String(pose.pitch);
  yawSlider.value = String(pose.yaw);

  rollValue.textContent = String(pose.roll);
  pitchValue.textContent = String(pose.pitch);
  yawValue.textContent = String(pose.yaw);
  poseBadge.textContent = pose.label;

  if (typeof nextPose.tx === "number") {
    targetPosition.x = nextPose.tx;
  }
  if (typeof nextPose.ty === "number") {
    targetPosition.y = nextPose.ty;
  }
  if (typeof nextPose.tz === "number") {
    targetPosition.z = nextPose.tz;
  }
}

function readAxisToken(token, values) {
  switch (token) {
    case "roll":
      return values.roll;
    case "-roll":
      return -values.roll;
    case "pitch":
      return values.pitch;
    case "-pitch":
      return -values.pitch;
    case "yaw":
      return values.yaw;
    case "-yaw":
      return -values.yaw;
    default:
      return 0;
  }
}

function mapOrientationByPreset(roll, pitch, yaw) {
  switch (orientationPreset) {
    case "calibrated-v1":
      return { roll, pitch, yaw };
    case "swap-rp":
      return { roll: pitch, pitch: roll, yaw };
    case "invert-roll":
      return { roll: -roll, pitch, yaw };
    case "invert-pitch":
      return { roll, pitch: -pitch, yaw };
    case "swap-rp-invert-roll":
      return { roll: -pitch, pitch: roll, yaw };
    case "swap-rp-invert-pitch":
      return { roll: pitch, pitch: -roll, yaw };
    case "default":
    default:
      return { roll, pitch, yaw };
  }
}

function readPoseFromSliders() {
  return {
    roll: Number(rollSlider.value),
    pitch: Number(pitchSlider.value),
    yaw: Number(yawSlider.value),
    tx: targetPosition.x,
    ty: targetPosition.y,
    tz: targetPosition.z,
    label: "manual",
  };
}

function pushTrailPoint(x, y, z) {
  const point = new THREE.Vector3(x, y, z);
  if (trailPoints.length > 0) {
    const last = trailPoints[trailPoints.length - 1];
    if (last.distanceTo(point) < visualization.minTrailStep) {
      return;
    }
  }

  trailPoints.push(point);
  if (trailPoints.length > maxTrailPoints) {
    trailPoints.shift();
  }
  trailGeometry.setFromPoints(trailPoints);
}

function clearTrail() {
  trailPoints.length = 0;
  trailGeometry.setFromPoints([new THREE.Vector3(0, 0, 0)]);
}

function applySliderPose() {
  setPose(readPoseFromSliders());
  statusText.textContent = "Updated local preview from page controls.";
}

function sendCurrentPoseToHost() {
  const payload = {
    source: "page",
    type: "pose",
    ...readPoseFromSliders(),
  };

  window.chrome.webview.postMessage(JSON.stringify(payload));
  appendPageLog(`Page -> C#: pose ${payload.roll}/${payload.pitch}/${payload.yaw}`);
  statusText.textContent = "Sent pose data to the C# host.";
}

for (const slider of [rollSlider, pitchSlider, yawSlider]) {
  slider.addEventListener("input", applySliderPose);
}

for (const slider of [viewYawSlider, viewPitchSlider, viewDistanceSlider]) {
  slider.addEventListener("input", () => {
    applyViewportFromControls();
    statusText.textContent = "Viewport adjusted.";
  });
}

for (const slider of [viewMotionScaleSlider, viewTrailStepSlider]) {
  slider.addEventListener("input", () => {
    applyViewportFromControls();
    statusText.textContent = "Visualization scale updated.";
  });
}

for (const slider of [
  moduleOffsetXSlider,
  moduleOffsetYSlider,
  moduleOffsetZSlider,
  moduleYawSlider,
  modulePitchSlider,
  moduleRollSlider,
]) {
  slider.addEventListener("input", () => {
    applyLayoutFromControls();
    statusText.textContent = "Module layout updated.";
  });
}

for (const selector of [devicePresetSelect, showTrailCheckbox]) {
  selector.addEventListener("change", () => {
    applyLayoutFromControls();
    statusText.textContent = "Device preview updated.";
  });
}

orientationPresetSelect.addEventListener("change", () => {
  orientationPreset = orientationPresetSelect.value;
  applySliderPose();
  appendPageLog(`Orientation preset: ${orientationPreset}`);
  statusText.textContent = `Mapping preset switched to ${orientationPreset}.`;
});

mapPitchSelect.addEventListener("change", () => {
  orientationAxisMap.pitch = mapPitchSelect.value;
  applySliderPose();
  appendPageLog(`Model Pitch <- ${orientationAxisMap.pitch}`);
  statusText.textContent = "Updated model pitch-axis mapping.";
});

mapYawSelect.addEventListener("change", () => {
  orientationAxisMap.yaw = mapYawSelect.value;
  applySliderPose();
  appendPageLog(`Model Yaw <- ${orientationAxisMap.yaw}`);
  statusText.textContent = "Updated model yaw-axis mapping.";
});

mapRollSelect.addEventListener("change", () => {
  orientationAxisMap.roll = mapRollSelect.value;
  applySliderPose();
  appendPageLog(`Model Roll <- ${orientationAxisMap.roll}`);
  statusText.textContent = "Updated model roll-axis mapping.";
});

sendToHostButton.addEventListener("click", sendCurrentPoseToHost);

resetMappingButton.addEventListener("click", () => {
  orientationAxisMap.pitch = "pitch";
  orientationAxisMap.yaw = "yaw";
  orientationAxisMap.roll = "-roll";

  mapPitchSelect.value = orientationAxisMap.pitch;
  mapYawSelect.value = orientationAxisMap.yaw;
  mapRollSelect.value = orientationAxisMap.roll;

  applySliderPose();
  appendPageLog("3D coordinate mapping reset to calibrated defaults.");
  statusText.textContent = "3D coordinate mapping reset (roll inverted).";
});

resetViewportButton.addEventListener("click", () => {
  viewportCamera.yawDeg = 34;
  viewportCamera.pitchDeg = 22;
  viewportCamera.distance = 6.2;
  visualization.motionScale = 8.0;
  visualization.minTrailStep = 0.018;

  viewYawSlider.value = String(viewportCamera.yawDeg);
  viewPitchSlider.value = String(viewportCamera.pitchDeg);
  viewDistanceSlider.value = String(viewportCamera.distance);
  viewMotionScaleSlider.value = String(visualization.motionScale);
  viewTrailStepSlider.value = String(visualization.minTrailStep);

  applyViewportFromControls();
  appendPageLog("Viewport reset to default camera.");
  statusText.textContent = "Viewport reset.";
});

resetLayoutButton.addEventListener("click", () => {
  moduleLayout.preset = "none";
  moduleLayout.offsetX = 0.0;
  moduleLayout.offsetY = 0.18;
  moduleLayout.offsetZ = 0.0;
  moduleLayout.yawDeg = 0;
  moduleLayout.pitchDeg = 0;
  moduleLayout.rollDeg = 0;
  moduleLayout.showTrail = true;

  devicePresetSelect.value = moduleLayout.preset;
  moduleOffsetXSlider.value = String(moduleLayout.offsetX);
  moduleOffsetYSlider.value = String(moduleLayout.offsetY);
  moduleOffsetZSlider.value = String(moduleLayout.offsetZ);
  moduleYawSlider.value = String(moduleLayout.yawDeg);
  modulePitchSlider.value = String(moduleLayout.pitchDeg);
  moduleRollSlider.value = String(moduleLayout.rollDeg);
  showTrailCheckbox.value = moduleLayout.showTrail ? "on" : "off";

  applyLayoutFromControls();
  appendPageLog("Device layout reset to defaults.");
  statusText.textContent = "Layout reset.";
});

resetPoseButton.addEventListener("click", () => {
  setPose({ roll: 0, pitch: 0, yaw: 0, tx: 0, ty: 0, tz: 0, label: "neutral" });
  clearTrail();
  statusText.textContent = "Pose reset to neutral.";
  appendPageLog("Local pose reset.");
});

clearPageLogButton.addEventListener("click", () => {
  pageLogLines.length = 0;
  pageLogLines.push("[page] Log cleared.");
  pageLog.textContent = pageLogLines[0];
  statusText.textContent = "Page log cleared.";
  clearTrail();
});

window.chrome.webview.addEventListener("message", (event) => {
  const raw = typeof event.data === "string" ? event.data : JSON.stringify(event.data);
  hostMessageText.textContent = raw;

  let payload = null;

  try {
    payload = JSON.parse(raw);
  } catch {
    statusText.textContent = "Received a plain text message from C#.";
    appendPageLog(`C# -> Page: ${raw}`);
    return;
  }

  if (payload.type === "pose") {
    setPose(payload);
    statusText.textContent = `Applied pose from C#: ${payload.label ?? "pose"}.`;
    const now = Date.now();
    if (now - lastPoseLogMs > 1000) {
      lastPoseLogMs = now;
      appendPageLog(`C# -> Page pose: ${payload.roll}/${payload.pitch}/${payload.yaw}`);
    }
    return;
  }

  statusText.textContent = "Received a JSON message from C#.";
  appendPageLog(`C# -> Page: ${raw}`);
});

const resizeObserver = new ResizeObserver(() => resizeScene());
resizeObserver.observe(viewport);

function animate() {
  const displayTargetX = targetPosition.x * visualization.motionScale;
  const displayTargetY = targetPosition.y * visualization.motionScale;
  const displayTargetZ = targetPosition.z * visualization.motionScale;

  cubeGroup.position.x = THREE.MathUtils.lerp(cubeGroup.position.x, displayTargetX + moduleLayout.offsetX, 0.2);
  cubeGroup.position.y = THREE.MathUtils.lerp(cubeGroup.position.y, displayTargetY + moduleLayout.offsetY, 0.2);
  cubeGroup.position.z = THREE.MathUtils.lerp(cubeGroup.position.z, displayTargetZ + moduleLayout.offsetZ, 0.2);

  const mountPitch = THREE.MathUtils.degToRad(moduleLayout.pitchDeg);
  const mountYaw = THREE.MathUtils.degToRad(moduleLayout.yawDeg);
  const mountRoll = THREE.MathUtils.degToRad(moduleLayout.rollDeg);
  cubeGroup.rotation.x = THREE.MathUtils.lerp(cubeGroup.rotation.x, targetRotation.x + mountPitch, 0.14);
  cubeGroup.rotation.y = THREE.MathUtils.lerp(cubeGroup.rotation.y, targetRotation.y + mountYaw, 0.14);
  cubeGroup.rotation.z = THREE.MathUtils.lerp(cubeGroup.rotation.z, targetRotation.z + mountRoll, 0.14);

  currentMarker.position.copy(cubeGroup.position);
  mountAnchor.position.set(moduleLayout.offsetX, moduleLayout.offsetY, moduleLayout.offsetZ);
  mountAxes.position.copy(mountAnchor.position);

  pushTrailPoint(cubeGroup.position.x, cubeGroup.position.y, cubeGroup.position.z);

  renderer.render(scene, camera);
  requestAnimationFrame(animate);
}

setPose({ roll: 0, pitch: 0, yaw: 0, label: "neutral" });
orientationPresetSelect.value = orientationPreset;
mapPitchSelect.value = orientationAxisMap.pitch;
mapYawSelect.value = orientationAxisMap.yaw;
mapRollSelect.value = orientationAxisMap.roll;
viewYawSlider.value = String(viewportCamera.yawDeg);
viewPitchSlider.value = String(viewportCamera.pitchDeg);
viewDistanceSlider.value = String(viewportCamera.distance);
viewMotionScaleSlider.value = String(visualization.motionScale);
viewTrailStepSlider.value = String(visualization.minTrailStep);
devicePresetSelect.value = moduleLayout.preset;
moduleOffsetXSlider.value = String(moduleLayout.offsetX);
moduleOffsetYSlider.value = String(moduleLayout.offsetY);
moduleOffsetZSlider.value = String(moduleLayout.offsetZ);
moduleYawSlider.value = String(moduleLayout.yawDeg);
modulePitchSlider.value = String(moduleLayout.pitchDeg);
moduleRollSlider.value = String(moduleLayout.rollDeg);
showTrailCheckbox.value = moduleLayout.showTrail ? "on" : "off";
applyViewportFromControls();
applyLayoutFromControls();
resizeScene();
animate();
appendPageLog("Three.js pose viewer loaded.");