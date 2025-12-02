using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LGCInspector2;
using System;
using System.Diagnostics; // Stopwatch 용 
using System.IO;
using System.Threading.Tasks;
using System.Windows.Threading; // DispatcherTimer용
using System.Windows.Forms; // FolderBrowserDialog

namespace LGCInspector2.ViewModels
{
    public class MainViewModel : ObservableObject
    {
        // 두 개의 엔진 인스턴스 (Cam1, Cam2)
        private InferenceEngine _engineCam1;
        private InferenceEngine _engineCam2;

        // [추가] 타이머 관련 객체
        private DispatcherTimer _uiTimer;
        private Stopwatch _stopwatch;

        // 화면에 표시할 경과 시간 문자열
        private string _elapsedTime = "00:00:00";
        public string ElapsedTime { get => _elapsedTime; set => SetProperty(ref _elapsedTime, value); }

        // 퍼센트 표시용 속성
        private int _progressPercent;
        public int ProgressPercent { get => _progressPercent; set => SetProperty(ref _progressPercent, value); }

        private string _sourceDir;
        public string SourceDir { get => _sourceDir; set => SetProperty(ref _sourceDir, value); }

        private string _statusMessage = "실행 파일 경로에 Cam01.onnx, Cam02.onnx 파일이 있어야 합니다.";
        public string StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }

        private int _totalCount;
        public int TotalCount { get => _totalCount; set => SetProperty(ref _totalCount, value); }

        private int _currentProgress;
        public int CurrentProgress { get => _currentProgress; set => SetProperty(ref _currentProgress, value); }

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                SetProperty(ref _isBusy, value);
                RunInspectionCommand.NotifyCanExecuteChanged();
            }
        }

        public IRelayCommand SelectFolderCommand { get; }
        public IRelayCommand RunInspectionCommand { get; }

        public MainViewModel()
        {
            _engineCam1 = new InferenceEngine();
            _engineCam2 = new InferenceEngine();

            SelectFolderCommand = new RelayCommand(SelectFolder);
            RunInspectionCommand = new RelayCommand(async () => await RunInspection(), () => !IsBusy);

            // [추가] 타이머 초기화
            _stopwatch = new Stopwatch();
            _uiTimer = new DispatcherTimer();
            _uiTimer.Interval = TimeSpan.FromMilliseconds(100); // 0.1초마다 갱신
            _uiTimer.Tick += UiTimer_Tick;

        }

        // [추가] 타이머 틱 이벤트 (화면 갱신용)
        private void UiTimer_Tick(object sender, EventArgs e)
        {
            // 스톱워치 시간을 가져와서 보기 좋은 문자열로 변환
            // 포맷 예시: 시:분:초
            ElapsedTime = _stopwatch.Elapsed.ToString(@"hh\:mm\:ss");
        }

        private void SelectFolder()
        {
            using (var dialog = new FolderBrowserDialog())
            {
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    SourceDir = dialog.SelectedPath;
                    StatusMessage = "폴더 선택 완료. 검사를 시작하세요.";
                }
            }
        }

        private async Task RunInspection()
        {
            if (string.IsNullOrEmpty(SourceDir) || !Directory.Exists(SourceDir))
            {
                StatusMessage = "이미지 폴더를 선택해주세요.";
                return;
            }

            // 모델 파일 자동 감지 (실행 파일 위치 기준)
            string modelPath1 = "Cam01.onnx";
            string modelPath2 = "Cam02.onnx";

            if (!File.Exists(modelPath1) || !File.Exists(modelPath2))
            {
                StatusMessage = "실행 파일 경로에 'Cam01.onnx'와 'Cam02.onnx'가 모두 있어야 합니다.";
                return;
            }

            IsBusy = true;

            // 변수 초기화
            CurrentProgress = 0;   // 0부터 다시 시작하도록 리셋
            ProgressPercent = 0;   // 퍼센트도 0으로 리셋
            _elapsedTime = "00:00:00"; // 시간 표시도 리셋
            OnPropertyChanged(nameof(ElapsedTime)); // 화면 갱신

            // 검사 시작 전 타이머 시작
            _stopwatch.Restart(); // 0부터 다시 시작
            _uiTimer.Start();     // UI 갱신 시작

            try
            {
                await Task.Run(() =>
                {
                    StatusMessage = "모델 로딩 중...";
                    _engineCam1.LoadModel(modelPath1);
                    _engineCam2.LoadModel(modelPath2);

                    // 결과 폴더 생성
                    string okPath = Path.Combine(SourceDir, "Result_OK");
                    string ngPath = Path.Combine(SourceDir, "Result_NG");
                    Directory.CreateDirectory(okPath);
                    Directory.CreateDirectory(ngPath);

                    // 이미지 파일 가져오기
                    var files = Directory.GetFiles(SourceDir, "*.*");
                    var imageFiles = Array.FindAll(files, f =>
                        f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase));

                    TotalCount = imageFiles.Length;
                    if (TotalCount == 0) return;

                    foreach (var file in imageFiles)
                    {
                        string fileName = Path.GetFileName(file);
                        string result = "";
                        InferenceEngine activeEngine = null;

                        // [요청사항 2] 파일명 기반 엔진 선택
                        if (fileName.Contains("_Lucid-1_"))
                        {
                            activeEngine = _engineCam1;
                        }
                        else if (fileName.Contains("_Lucid-2_"))
                        {
                            activeEngine = _engineCam2;
                        }
                        else
                        {
                            // 규칙에 안 맞는 파일은 건너뜀 (로그만 남김)
                            StatusMessage = $"Skip: {fileName} (파일명 규칙 불일치)";
                            CurrentProgress++;
                            continue;
                        }

                        try
                        {
                            // 모델 재검증으로 주석 처리_251201
                            // [요청사항 1] 빈 컨베이어 체크 (엔진 상관없이 로직 동일하므로 아무 엔진이나 사용하거나 정적 메소드로 분리 가능)
                            // 여기서는 activeEngine의 메소드 호출
                            bool hasProduct = activeEngine.IsProductExist(file, meanThreshold: 50.0, stdThreshold: 5.0);

                            if (!hasProduct)
                            {
                                // 빈 컨베이어 -> OK 처리 (Empty 로그 붙임)
                                result = "OK";
                                // (옵션: 파일명에 _Empty 태그를 붙여서 저장할 수도 있음)
                            }
                            else
                            {
                                // 제품 있음 -> AI 검사 수행
                                result = activeEngine.Predict(file);
                            }

                            // 결과에 따른 파일 이동/복사
                            string destFolder = (result == "OK") ? okPath : ngPath;
                            string destFile = Path.Combine(destFolder, fileName);
                            File.Copy(file, destFile, true);

                            CurrentProgress++;
                            // (현재개수 / 총개수 * 100) -> 정수로 변환
                            ProgressPercent = (int)((double)CurrentProgress / TotalCount * 100);
                            StatusMessage = $"[{result}] {fileName} (Cam{(activeEngine == _engineCam1 ? "1" : "2")})";

                            //// [수정된 부분] OK/NG 판정 대신 점수만 가져오기
                            //float score = activeEngine.GetRawScore(file);

                            //// 결과 폴더로 이동하지 말고, 그냥 로그만 찍어서 확인
                            //// 예: "파일: abc.jpg / 점수: 4.52"
                            //StatusMessage = $"파일: {fileName} / 점수: {score:F4}";

                            //// 너무 빨리 지나가면 못 보니까, 콘솔 출력이나 잠시 멈춤이 필요할 수 있음
                            //// 또는 텍스트 파일에 로그를 쓰는 것이 가장 좋음
                            //File.AppendAllText(Path.Combine(SourceDir, "ScoreLog.txt"), $"{fileName}\t{score:F4}\n");

                            
                        }
                        catch (Exception ex)
                        {
                            StatusMessage = $"Err: {fileName} - {ex.Message}";
                        }
                    }
                });
                // 검사 완료 메시지에 최종 시간 포함
                string finalTime = _stopwatch.Elapsed.ToString(@"hh\:mm\:ss");
                StatusMessage = $"검사 완료! (총 {TotalCount}장, 검사 시간: {_stopwatch.Elapsed.ToString(@"hh\:mm\:ss")})";
            }
            catch (Exception ex)
            {
                StatusMessage = $"치명적 오류: {ex.Message}";
            }
            finally
            {
                // 타이머 정지
                _uiTimer.Stop();
                _stopwatch.Stop();

                IsBusy = false;
                _engineCam1.Dispose();
                _engineCam2.Dispose();
            }
        }
    }
}
