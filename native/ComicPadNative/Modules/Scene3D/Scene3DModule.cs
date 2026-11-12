// 3D 소재 · 버튜버 모듈 등록 (웹 판 register3DCommands·initVtuber 와 같은 메뉴 이름)
namespace ComicPadNative.Modules.Scene3D;

public sealed class Scene3DModule : IModule
{
    public void Init()
    {
        bool HasDoc() => App.Doc != null;
        Registry.Menu("만화/-3d", null, order: 900);
        Registry.Menu("만화/3D 소재 (데생 인형·모델 합성)…", Scene3DWindow.OpenFromMenu, enabled: HasDoc, order: 910);
        Registry.Menu("AI/-vt", null, order: 200);
        Registry.Menu("AI/버튜버 스튜디오 (웹캠·마이크로 캐릭터 움직이기)…", () => Vtuber.Studio(), enabled: HasDoc, order: 210);
        Registry.Menu("AI/버튜버 아바타 만들기 (레이어를 부위로)…", Vtuber.SetupDialog, enabled: HasDoc, order: 220);
        Registry.Menu("AI/버튜버 아바타 파일 열기 (.cpvt)…", Vtuber.OpenAvatarFile, order: 230);
    }
}
