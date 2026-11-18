// 버튜버 리깅 모듈 등록: 메뉴 "AI/버튜버 리깅 편집기…" + 버튜버 스튜디오가 메쉬 리그(작품의 vtRig, .cpvt 의 rig2)를 쓰도록 연결.
using ComicPadNative.Modules.Scene3D;

namespace ComicPadNative.Modules.VRig2;

public sealed class VRig2Module : IModule
{
    public void Init()
    {
        Registry.Menu("AI/버튜버 리깅 편집기 (메쉬·디포머·파라미터)…", RigEditorForm.Open, enabled: () => App.Doc != null, order: 215);
        Vtuber.AltFromDoc = d => RigPlayer.FromDoc(d);
        Vtuber.AltFromFile = n => RigPlayer.FromFile(n);
        Vtuber.OpenRigEditor = RigEditorForm.Open;
    }
}
