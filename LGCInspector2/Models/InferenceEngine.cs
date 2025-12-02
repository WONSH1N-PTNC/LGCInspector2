using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp; // OpenCV 핵심 네임스페이스
using System;
using System.Collections.Generic;
using System.Linq;

namespace LGCInspector2.ViewModels
{
    public class InferenceEngine : IDisposable
    {
        private InferenceSession _session;

        // ImageNet 정규화 값
        private readonly float[] _mean = { 0.485f, 0.456f, 0.406f };
        private readonly float[] _std = { 0.229f, 0.224f, 0.225f };

        public void LoadModel(string modelPath)
        {
            try
            {
                var options = new SessionOptions();
                _session = new InferenceSession(modelPath, options);
            }
            catch (Exception ex)
            {
                throw new Exception($"모델 로드 실패 ({modelPath}): {ex.Message}");
            }
        }

        public bool IsProductExist(string imagePath, double meanThreshold = 50.0, double stdThreshold = 5.0)
        {
            // [수정] 명시적으로 OpenCvSharp.Cv2 사용
            // ImreadModes.Grayscale을 올바르게 인식하도록 수정
            using (var src = Cv2.ImRead(imagePath, ImreadModes.Grayscale))
            {
                if (src.Empty()) return false;

                // 1. 평균(Mean)과 표준편차(StdDev) 계산
                Cv2.MeanStdDev(src, out Scalar mean, out Scalar stdDev);

                double meanVal = mean.Val0;
                double stdVal = stdDev.Val0;

                // 2. 조건 판별
                if (meanVal > meanThreshold && stdVal > stdThreshold)
                {
                    return true;
                }
                else
                {
                    return false;
                }
            }
        }

        public string Predict(string imagePath)
        {
            if (_session == null) throw new Exception("모델이 로드되지 않았습니다.");

            // 1. 전처리 실행
            var inputTensor = PreprocessWithOpenCV(imagePath, 224, 224);

            // 2. 입력 설정
            var inputMeta = _session.InputMetadata;
            var inputName = inputMeta.Keys.First();
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(inputName, inputTensor)
            };

            // 3. 추론 실행
            using (var results = _session.Run(inputs))
            {
                var outputData = results.First().AsEnumerable<float>().ToArray();

                // [0]=OK 확률, [1]=NG 확률 (Softmax 가정)
                if (outputData.Length >= 2)
                {
                    float okScore = outputData[0];
                    float ngScore = outputData[1];
                    return ngScore > okScore ? "NG" : "OK";
                }
                else if (outputData.Length == 1)
                {
                    // 단일 Score인 경우
                    return outputData[0] > 0.8f ? "NG" : "OK";
                }

                return "ERROR";
            }
        }

        private DenseTensor<float> PreprocessWithOpenCV(string path, int width, int height)
        {
            // ImreadModes.Color 사용
            using (var src = Cv2.ImRead(path, ImreadModes.Color))
            using (var resized = new Mat())
            {
                // [수정] OpenCvSharp.Size 구조체 명시적 사용 (System.Drawing.Size와 충돌 방지)
                Cv2.Resize(src, resized, new OpenCvSharp.Size(width, height), 0, 0, InterpolationFlags.Cubic);

                // BGR -> RGB 변환
                Cv2.CvtColor(resized, resized, ColorConversionCodes.BGR2RGB);

                var tensor = new DenseTensor<float>(new[] { 1, 3, height, width });

                // 픽셀 데이터 접근
                var indexer = resized.GetGenericIndexer<Vec3b>();

                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        Vec3b pixel = indexer[y, x];

                        // 정규화 로직
                        //tensor[0, 0, y, x] = ((pixel.Item0 / 255.0f) - _mean[0]) / _std[0]; // R
                        //tensor[0, 1, y, x] = ((pixel.Item1 / 255.0f) - _mean[1]) / _std[1]; // G
                        //tensor[0, 2, y, x] = ((pixel.Item2 / 255.0f) - _mean[2]) / _std[2]; // B
                        // 전처리 끄기
                        tensor[0, 0, y, x] = pixel.Item0 / 255.0f; // R
                        tensor[0, 1, y, x] = pixel.Item1 / 255.0f; // G
                        tensor[0, 2, y, x] = pixel.Item2 / 255.0f; // B
                    }
                }
                return tensor;
            }
        }
        public float GetRawScore(string imagePath)
        {
            if (_session == null) throw new Exception("모델 로드 필요");

            // 전처리 (Python 학습 시 ImageNet 정규화를 썼으므로 C#도 동일하게 적용)
            // 앞서 작성해드린 PreprocessWithOpenCV 함수 사용
            var inputTensor = PreprocessWithOpenCV(imagePath, 224, 224);

            var inputMeta = _session.InputMetadata;
            var inputName = inputMeta.Keys.First();
            var inputs = new List<NamedOnnxValue>
    {
        NamedOnnxValue.CreateFromTensor(inputName, inputTensor)
    };

            using (var results = _session.Run(inputs))
            {
                var outputData = results.First().AsEnumerable<float>().ToArray();

                // PatchCore ONNX 출력:
                // [0] = Anomaly Score (우리가 필요한 값)
                // [1] = Anomaly Map (히트맵, 여기선 불필요)

                return outputData[0];
            }
        }
        public void Dispose()
        {
            _session?.Dispose();
        }
    }
}