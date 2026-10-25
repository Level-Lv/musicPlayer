using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace musicPlayer
{
    /// <summary>
    /// WPF 原生没有 GridLength 类型的动画，这里补一个（只支持像素值之间的过渡）。
    /// 用于侧栏收起/展开时的丝滑宽度过渡。
    /// 用法：column.BeginAnimation(ColumnDefinition.WidthProperty, new GridLengthAnimation { To = 0, ... });
    /// </summary>
    public class GridLengthAnimation : AnimationTimeline
    {
        public static readonly DependencyProperty FromProperty =
            DependencyProperty.Register(nameof(From), typeof(double), typeof(GridLengthAnimation));

        public static readonly DependencyProperty ToProperty =
            DependencyProperty.Register(nameof(To), typeof(double), typeof(GridLengthAnimation));

        public static readonly DependencyProperty EasingFunctionProperty =
            DependencyProperty.Register(nameof(EasingFunction), typeof(IEasingFunction), typeof(GridLengthAnimation));

        /// <summary>起始宽度（像素）；null 则取当前值。</summary>
        public double? From
        {
            get => (double?)GetValue(FromProperty);
            set => SetValue(FromProperty, value);
        }

        /// <summary>目标宽度（像素）。</summary>
        public double? To
        {
            get => (double?)GetValue(ToProperty);
            set => SetValue(ToProperty, value);
        }

        /// <summary>缓动函数（丝滑的关键）。</summary>
        public IEasingFunction? EasingFunction
        {
            get => (IEasingFunction?)GetValue(EasingFunctionProperty);
            set => SetValue(EasingFunctionProperty, value);
        }

        public override Type TargetPropertyType => typeof(GridLength);

        protected override Freezable CreateInstanceCore() => new GridLengthAnimation();

        public override object GetCurrentValue(object defaultOriginValue,
            object defaultDestinationValue, AnimationClock clock)
        {
            double from = From ?? (defaultOriginValue is GridLength gl ? gl.Value : 0);
            double to = To ?? (defaultDestinationValue is GridLength gl2 ? gl2.Value : 0);
            if (from < 0) from = 0;
            if (to < 0) to = 0;

            double progress = clock.CurrentProgress.GetValueOrDefault(); // 0 ~ 1
            if (EasingFunction != null) progress = EasingFunction.Ease(progress);

            return new GridLength(from + (to - from) * progress, GridUnitType.Pixel);
        }
    }
}
