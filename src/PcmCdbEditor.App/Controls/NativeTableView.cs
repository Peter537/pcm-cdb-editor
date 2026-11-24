using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using WinUI.TableView;

namespace PcmCdbEditor.App.Controls;

public sealed partial class NativeTableView : TableView
{
    private ScrollViewer? _body;

    protected override void OnApplyTemplate()
    {
        if (_body is not null)
        {
            _body.ViewChanged -= Body_ViewChanged;
        }

        base.OnApplyTemplate();
        _body = GetTemplateChild("ScrollViewer") as ScrollViewer;
        if (_body is not null)
        {
            _body.ViewChanged += Body_ViewChanged;
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new NativeTableViewAutomationPeer(this);

    internal void SetHorizontalPosition(double offset)
    {
        double position = Math.Clamp(offset, 0, _body?.ScrollableWidth ?? 0);
        SetValue(HorizontalOffsetProperty, position);
    }

    private void Body_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs args)
    {
        // TableView arranges both headers and cells using its own horizontal offset.
        // Keep the underlying ScrollViewer's physical scrolling vertical only.
        if (_body is { HorizontalOffset: not 0 } body)
        {
            body.ChangeView(0, null, null, disableAnimation: true);
        }
    }

    internal void FinishExternalCommit(TableViewCell cell)
    {
        EndEditing(cell, TableViewEditAction.Commit);
        SetIsEditing(this, false);
    }

    // TableView 1.4.1 exposes the edit events but has no public way to end an edit.
    // These are the same native lifecycle methods used by its Enter key handler.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "EndEditing")]
    private static extern void EndEditing(TableViewCell cell, TableViewEditAction action);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "SetIsEditing")]
    private static extern void SetIsEditing(TableView table, bool value);

    private sealed partial class NativeTableViewAutomationPeer(NativeTableView owner)
        : ListViewAutomationPeer(owner), IScrollProvider
    {
        private IScrollProvider? VerticalScroll => owner._body is { } body
            ? FrameworkElementAutomationPeer.CreatePeerForElement(body)?.GetPattern(PatternInterface.Scroll) as IScrollProvider
            : null;

        private double ScrollableWidth => owner._body?.ScrollableWidth ?? 0;

        public bool HorizontallyScrollable => ScrollableWidth > 0;
        public double HorizontalScrollPercent => HorizontallyScrollable ? 100 * owner.HorizontalOffset / ScrollableWidth : -1;
        public double HorizontalViewSize => owner._body is { ExtentWidth: > 0 } body
            ? Math.Min(100, 100 * body.ViewportWidth / body.ExtentWidth)
            : 100;
        public bool VerticallyScrollable => VerticalScroll?.VerticallyScrollable == true;
        public double VerticalScrollPercent => VerticalScroll?.VerticalScrollPercent ?? -1;
        public double VerticalViewSize => VerticalScroll?.VerticalViewSize ?? 100;

        protected override object GetPatternCore(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Scroll ? this : base.GetPatternCore(patternInterface);

        public void Scroll(ScrollAmount horizontalAmount, ScrollAmount verticalAmount)
        {
            double delta = horizontalAmount switch
            {
                ScrollAmount.LargeDecrement => -(owner._body?.ViewportWidth ?? 0),
                ScrollAmount.SmallDecrement => -30,
                ScrollAmount.NoAmount => 0,
                ScrollAmount.SmallIncrement => 30,
                ScrollAmount.LargeIncrement => owner._body?.ViewportWidth ?? 0,
                _ => throw new ArgumentOutOfRangeException(nameof(horizontalAmount)),
            };

            if (delta != 0)
            {
                owner.SetHorizontalPosition(owner.HorizontalOffset + delta);
            }
            if (verticalAmount != ScrollAmount.NoAmount)
            {
                VerticalScroll?.Scroll(ScrollAmount.NoAmount, verticalAmount);
            }
        }

        public void SetScrollPercent(double horizontalPercent, double verticalPercent)
        {
            if (horizontalPercent != -1)
            {
                ArgumentOutOfRangeException.ThrowIfLessThan(horizontalPercent, 0);
                ArgumentOutOfRangeException.ThrowIfGreaterThan(horizontalPercent, 100);
                owner.SetHorizontalPosition(ScrollableWidth * (horizontalPercent / 100));
            }
            if (verticalPercent != -1)
            {
                VerticalScroll?.SetScrollPercent(-1, verticalPercent);
            }
        }
    }
}
