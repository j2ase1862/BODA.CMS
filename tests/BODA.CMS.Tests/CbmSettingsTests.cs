using BODA.CMS.Collector;
using Xunit;

namespace BODA.CMS.Tests
{
    /// <summary>Collector:Cbm 설정 — 작업 사이클 기반 기준선 학습창 계산.</summary>
    public class CbmSettingsTests
    {
        [Fact]
        public void 기본값은_60초다()
        {
            Assert.Equal(60, new CbmSettings().EffectiveLearningSeconds());
        }

        [Fact]
        public void 사이클을_설정하면_사이클_곱하기_횟수가_학습창이_된다()
        {
            var s = new CbmSettings { CycleSeconds = 45, CyclesToLearn = 3 };
            Assert.Equal(135, s.EffectiveLearningSeconds());
        }

        [Fact]
        public void 사이클이_정수가_아니면_올림한다()
        {
            var s = new CbmSettings { CycleSeconds = 20.5, CyclesToLearn = 3 };
            Assert.Equal(62, s.EffectiveLearningSeconds()); // ceil(61.5)
        }

        [Fact]
        public void 학습창_직접_지정이_사이클_계산보다_우선한다()
        {
            var s = new CbmSettings { CycleSeconds = 45, CyclesToLearn = 3, LearningSeconds = 300 };
            Assert.Equal(300, s.EffectiveLearningSeconds());
        }

        [Fact]
        public void 하한은_30초다_기준선이_무의미하게_짧아지지_않게()
        {
            Assert.Equal(30, new CbmSettings { LearningSeconds = 5 }.EffectiveLearningSeconds());
            Assert.Equal(30, new CbmSettings { CycleSeconds = 5, CyclesToLearn = 1 }.EffectiveLearningSeconds());
        }

        [Fact]
        public void 사이클_횟수가_0이하면_1로_취급한다()
        {
            var s = new CbmSettings { CycleSeconds = 50, CyclesToLearn = 0 };
            Assert.Equal(50, s.EffectiveLearningSeconds());
        }

        [Fact]
        public void CbmOptions_로_변환하면_학습_집계수에_반영된다()
        {
            var s = new CbmSettings { CycleSeconds = 60, CyclesToLearn = 3 };
            Assert.Equal(180, s.ToCbmOptions().LearningAggregates);
        }
    }
}
