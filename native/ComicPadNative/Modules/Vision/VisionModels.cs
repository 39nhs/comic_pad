// 네이티브 판 전용 비전 모델 목록 (웹 판의 MediaPipe .task 는 TFLite 라 ONNX Runtime 으로 못 돌림 → 같은 Google 모델의 ONNX 변환본).
// 모두 Apache-2.0. 주소는 커밋·릴리스 고정, 크기·SHA-256 은 한 번 받아 확인한 값 (AiRuntime.Download 가 똑같이 검사).
using ComicPadNative.Modules.AI;

namespace ComicPadNative.Modules.Vision;

public static class VisionModels
{
    public const string Pose = "pose-blaze", Hand = "hand-blaze", Face = "face-blaze";

    const string UPose = "https://huggingface.co/unity/inference-engine-blaze-pose/resolve/d96e13766db93eaaac6ad398d7b01f1c94c7ff6e/";
    const string UHand = "https://huggingface.co/unity/inference-engine-blaze-hand/resolve/1e4394f13926e0b61c4a2d8f24ce7a78319d4d21/";
    const string UFace = "https://huggingface.co/unity/inference-engine-blaze-face/resolve/6b3dcfdd87c36a157477329821bab552eddc8f79/";
    const string PMesh = "https://github.com/PINTO0309/facemesh_onnx_tensorrt/releases/download/1.0.0/";

    public static readonly AiModel[] All =
    {
        new AiModel
        {
            Id = Pose, Task = "pose", Name = "BlazePose ONNX (사진 → 인형 자세, 네이티브)", License = "Apache-2.0", Commercial = "yes",
            Source = "https://huggingface.co/unity/inference-engine-blaze-pose",
            Note = "Google MediaPipe BlazePose(Apache-2.0)를 Unity 가 ONNX 로 옮긴 것",
            Files =
            {
                ("pose_detection.onnx", 15058101, UPose + "models/pose_detection.onnx", "72081da8481170bc6d8fafa716455ee210b61a8cefed84c67fcbbf889a4c38cf"),
                ("pose_landmarks_detector_full.onnx", 12765405, UPose + "models/pose_landmarks_detector_full.onnx", "ae17ee8f076a5bbc28f65b939f46139c10f10c51ec4392a011e56d06d3f76c5d"),
                ("anchors.csv", 81144, UPose + "data/anchors.csv", "57598dc8d5b6881a820046aa1fcb866401aa922e7c75abfa8bab7b14c3e79f77"),
            },
        },
        new AiModel
        {
            Id = Hand, Task = "hand", Name = "BlazeHand ONNX (사진 → 손 모양, 네이티브)", License = "Apache-2.0", Commercial = "yes",
            Source = "https://huggingface.co/unity/inference-engine-blaze-hand",
            Note = "Google MediaPipe Hands(Apache-2.0)를 Unity 가 ONNX 로 옮긴 것",
            Files =
            {
                ("hand_detector.onnx", 4589374, UHand + "models/hand_detector.onnx", "c620c7c17de68a6568d0ce9e1ee1335531b7c7a6567dfd1150856e20921cbba9"),
                ("hand_landmarks_detector.onnx", 10903207, UHand + "models/hand_landmarks_detector.onnx", "e18a95135b40c732ea53d2dd6af66cbec6d3f8bf0296bb529a4c4be0e8349ec1"),
                ("anchors.csv", 68880, UHand + "data/anchors.csv", "a59985569c7ea0347b74a5ac912520ac2e051cf7b633c0c4a1bb00b70ddb7180"),
            },
        },
        new AiModel
        {
            Id = Face, Task = "face", Name = "BlazeFace + FaceMesh ONNX (버튜버 얼굴 추적, 네이티브)", License = "Apache-2.0", Commercial = "yes",
            Ep = "wasm",      // 아주 작은 모델이라 CPU 가 더 빠름 (그래픽 가속은 처음 한 장 준비에 오래 걸림)
            Source = "https://github.com/PINTO0309/facemesh_onnx_tensorrt",
            Note = "얼굴 찾기: Unity 의 BlazeFace ONNX(huggingface.co/unity/inference-engine-blaze-face), 얼굴 그물 468점: PINTO0309 의 FaceMesh ONNX. 둘 다 Google MediaPipe(Apache-2.0) 변환본",
            Files =
            {
                ("blaze_face_short_range.onnx", 418465, UFace + "models/blaze_face_short_range.onnx", "587fa34c93de9a523e691669d2405671d67faad86e595e9a63c6d3db401f18f4"),
                ("anchors.csv", 32256, UFace + "data/anchors.csv", "f56366c69183510e6bcee214d26793d9148cb6a5ab6a4199c8c8c61690e19a86"),
                ("face_mesh_Nx3x192x192_post.onnx", 2461455, PMesh + "face_mesh_Nx3x192x192_post.onnx", "ae6ada9004f22be3ab6bab8cbfbc8b795f8595f69e5eea77b95cb2fc344c04eb"),
            },
        },
    };

    public static void RegisterAll() { foreach (var m in All) AiRuntime.Register(m); }

    public static AiModel Get(string id) => AiRuntime.Registry().FirstOrDefault(m => m.Id == id);
}
