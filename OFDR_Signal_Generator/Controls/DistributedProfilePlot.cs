using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using OFDR_Signal_Core.Models;
using OFDR_Signal_Core.Processing;

namespace OFDR_Signal_Generator.Controls
{
    public sealed class DistributedProfilePlot : FrameworkElement
    {
        public static readonly DependencyProperty ResultProperty=DependencyProperty.Register(nameof(Result),typeof(DistributedComparisonResult),typeof(DistributedProfilePlot),new FrameworkPropertyMetadata(null,FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty PairProperty=DependencyProperty.Register(nameof(Pair),typeof(string),typeof(DistributedProfilePlot),new FrameworkPropertyMetadata("상온 반복",FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty IsShiftProperty=DependencyProperty.Register(nameof(IsShift),typeof(bool),typeof(DistributedProfilePlot),new FrameworkPropertyMetadata(false,FrameworkPropertyMetadataOptions.AffectsRender));
        public DistributedComparisonResult Result {get=>(DistributedComparisonResult)GetValue(ResultProperty);set=>SetValue(ResultProperty,value);}
        public string Pair {get=>(string)GetValue(PairProperty);set=>SetValue(PairProperty,value);}
        public bool IsShift {get=>(bool)GetValue(IsShiftProperty);set=>SetValue(IsShiftProperty,value);}
        private static readonly Brush B=new SolidColorBrush(Color.FromRgb(186,97,35));
        private static readonly Brush C=new SolidColorBrush(Color.FromRgb(0,128,128));
        private Rect plot;
        public DistributedProfilePlot(){MinHeight=220;MouseMove+=Hover;MouseDown+=(s,e)=>{if(e.ClickCount==2&&Result!=null){new Window{Title=Pair+" · "+(IsShift?"Δλ 후보":"최대 ZNCC"),Width=1100,Height=650,Content=new DistributedProfilePlot{Result=Result,Pair=Pair,IsShift=IsShift,Margin=new Thickness(15)},Owner=Window.GetWindow(this)}.Show();}};}
        private void Text(DrawingContext d,string s,double x,double y,double size=12,Brush brush=null)
        {d.DrawText(new FormattedText(s,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),size,brush??Brushes.DarkSlateGray,VisualTreeHelper.GetDpi(this).PixelsPerDip),new Point(x,y));}
        protected override void OnRender(DrawingContext d)
        {
            base.OnRender(d);d.DrawRectangle(Brushes.White,null,new Rect(RenderSize));if(ActualWidth<180||ActualHeight<140)return;
            Text(d,Pair+" · "+(IsShift?"Δλ 후보 (pm; 대칭 로그)":"최대 ZNCC (무차원)"),12,5,14);
            Text(d,"B 명목축 ┄",12,28,11,B);Text(d,"C AUX축 ─",115,28,11,C);
            plot=new Rect(62,55,ActualWidth-82,ActualHeight-105);
            if(Result==null){Text(d,"처리 실행 또는 저장된 결과를 불러오세요.",70,100);return;}
            var settings=Result.Config.StageComparison.Document;
            var curves=Result.Curves.Where(c=>c.Pair==Pair).ToArray();
            double xmin=settings.StartMeters,xmax=settings.EndMeters;
            double limit=Math.Max(1,settings.MaximumShiftPm);
            Func<double,double> transform=v=>IsShift?Math.Sign(v)*Math.Log10(1+Math.Abs(v)):v;
            double ymin=IsShift?transform(-limit):0,ymax=IsShift?transform(limit):1;
            Func<double,double> xx=v=>plot.Left+(v-xmin)/(xmax-xmin)*plot.Width;
            Func<double,double> yy=v=>plot.Bottom-(transform(v)-ymin)/(ymax-ymin)*plot.Height;
            var grid=new Pen(Brushes.Gainsboro,1);
            var ticks=IsShift?new[]{-limit,-10d,-1d,0d,1d,10d,limit}.Where(v=>Math.Abs(v)<=limit).Distinct().OrderBy(v=>v).ToArray():new[]{0d,.25,.5,.75,1};
            foreach(var t in ticks){double y=yy(t);d.DrawLine(grid,new Point(plot.Left,y),new Point(plot.Right,y));Text(d,t.ToString("G3",CultureInfo.InvariantCulture),6,y-8,11);}
            for(int i=0;i<=5;i++){double v=xmin+(xmax-xmin)*i/5;double x=xx(v);d.DrawLine(grid,new Point(x,plot.Top),new Point(x,plot.Bottom));Text(d,v.ToString("G3",CultureInfo.InvariantCulture),x-12,plot.Bottom+5,11);}
            Text(d,"OFDR 상대 거리 (m; 입구 미등록) · 더블클릭 확대",62,ActualHeight-22,11);
            d.PushClip(new RectangleGeometry(plot));
            var marker=new Pen(Brushes.SlateGray,1){DashStyle=DashStyles.Dot};
            d.DrawLine(marker,new Point(xx(18.14),plot.Top),new Point(xx(18.14),plot.Bottom));
            if(!IsShift)d.DrawLine(marker,new Point(plot.Left,yy(settings.MinimumZncc)),new Point(plot.Right,yy(settings.MinimumZncc)));
            foreach(var curve in curves){var brush=curve.Condition=="B"?B:C;var pen=new Pen(brush,1.4);if(curve.Condition=="B")pen.DashStyle=DashStyles.Dash;Point? last=null;
                foreach(var p in curve.Points){double value=IsShift?p.CandidateShiftPm:p.PeakZncc;if(!ModeHopSettings.IsFinite(value)){last=null;continue;}var point=new Point(xx(p.DistanceMeters),yy(value));
                    if(IsShift){d.PushOpacity(p.Valid?1:.22);d.DrawEllipse(brush,null,point,p.Valid?3.2:2,p.Valid?3.2:2);d.Pop();}
                    else{if(last.HasValue)d.DrawLine(pen,last.Value,point);last=point;}
                }
            }
            d.Pop();
        }
        private void Hover(object sender,MouseEventArgs e)
        {
            if(Result==null||!plot.Contains(e.GetPosition(this))) {ToolTip=null;return;}
            var c=Result.Config.StageComparison.Document;double z=c.StartMeters+(e.GetPosition(this).X-plot.Left)/plot.Width*(c.EndMeters-c.StartMeters);
            ToolTip=string.Join("\n",Result.Curves.Where(v=>v.Pair==Pair).Select(v=>{var p=v.Points.OrderBy(q=>Math.Abs(q.DistanceMeters-z)).First();return string.Format(CultureInfo.InvariantCulture,"{0}: {1:F3} m · Δλ={2:G5} pm · ZNCC={3:F4} · 격차={4:F4} · {5}",v.Condition,p.DistanceMeters,p.CandidateShiftPm,p.PeakZncc,p.Separation,p.Status);}));
        }
    }
}
