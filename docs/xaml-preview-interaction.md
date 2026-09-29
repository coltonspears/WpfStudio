# Interacting with the XAML preview

Choose **Interact** after a successful preview to use its existing WPF view. Choose **Inspect**, or press **F8** while focus is in that view, to detach it and update the snapshot. The tree, property inspector and unapplied property draft remain available. **Update snapshot** rereads the current view; **Refresh** recreates it and resets its state.

Interaction uses the same objects and native presentation source as the preview. Source mode retains its source-loaded controls. Compiled mode retains its actual Window, Page or UserControl, code-behind, DataContext, scenario and temporary property overrides. A compiled Window remains a Window ancestor after embedding. The preview still does not run the application's startup path or apply unsaved XAML to its compiled assembly.

The native viewport uses actual size (100%). Its surrounding scrollbars move the view within a native clipping container. Keyboard focus reveals a supported control's cached layout box with the smallest scroll needed on each axis. A larger control that already overlaps the viewport stays in place, and newer manual scrolling takes precedence. The **Zoom** setting and selection/layout overlays apply to the inspection snapshot. Returning to Inspect rereads the current tree and properties while keeping the latest draft for a surviving selected property.

Interaction is temporarily hidden when an IDE overlay is open, the designer is auto-hidden or its owner window loses activation. Successful detach closes owned WPF popups and preserves the view for reattachment. Destroying a container with a pending attachment may stop the preview; choose **Refresh** to recreate it when prompted. Closing the preview, editing its source, changing configuration or replacing a render revokes the previous surface.

## Move and resize authored elements

In **Source** preview, choose **Inspect**, select an element, and enable **Edit layout**. Drag the selected frame to move it or one of its eight handles to resize it. A translucent outline shows the proposed bounds without changing the running preview. **Snap to layout** aligns pointer gestures to nearby parent, slot and sibling edges or centers; hold **Alt** to bypass it.

With the preview canvas focused, use arrow keys to nudge by one DIP, **Shift+arrows** for ten DIPs, or **Ctrl+arrows** to resize from the bottom-right corner. Keyboard nudges bypass snapping. **Enter** reviews the keyboard gesture; releasing the pointer reviews a drag. **Escape** cancels the draft.

The review shows one XAML change. **Apply changes** updates the unsaved editor buffer; **Undo workspace edit** restores the whole gesture. Save normally to write the file. With Live preview disabled, choose **Refresh** to render the edited buffer.

This increment supports direct authored children of framework **Canvas** and **Grid** panels. Canvas edits preserve the active leading or trailing anchors. Grid edits retain the existing row, column and spans, updating margins and only assigning explicit dimensions on resized axes. Existing alignment and untouched Auto dimensions are retained. New local values can override styles; the review identifies that consequence.

The preview host verifies the source element, parent, coordinate frame and property observation before review and again before applying. A changed source, selection, preview or layout cancels the operation. Bindings/resources on layout properties, animations, coercion, design-time overrides, template instances, generated containers, transformed elements, right-to-left frames and compiled previews do not provide an editable context. The status explains unavailable selections. Reparenting, changing Grid tracks, toolbox insertion and general composition remain future work.

See the [feature tour](xaml-feature-tour.md#move-and-resize-with-a-source-review) for actual application captures. Automated gesture and loaded-app checks do not establish physical-input acceptance.

## Process and window lifetime

The preview process and editor verify the named-pipe peer's process ID and a per-launch session ID. Each rendered surface has a separate identity. Every native operation also carries the local container's handle, process ID, unpredictable lease token and an increasing sequence. The host checks the actual window owner, lease property and DPI awareness before parenting its own window. Only the editor creates and destroys the container; only the preview host manipulates the preview window.

Both windows require matching Per-Monitor V2 awareness. The host converts the rendered DIP size to the container's current physical pixels and clamps scrolling to its content. DPI incompatibility is reported instead of forcing cross-awareness parenting.

Keyboard entry carries a separate short-lived grant on the editor's container. Later editor input, focus changes or container/session changes revoke that grant. The host checks it immediately before attempting entry and reports whether it found a tab stop. The editor continues traversal only for the matching, still-current entry request. Arrow navigation does not request a Tab exit.

At a Tab boundary, the preview synchronously asks the current editor container to move focus. A fresh attachment token, temporary property on the host-owned window and increasing call sequence identify that request; the editor also checks actual native ownership, parenting and current focus. It replies only after traversal returns, so repeated and opposite-direction boundary calls are not collapsed into a latest-only heartbeat notification. An uncertain result stops native interaction instead of replaying the request.

Focus geometry is sampled separately from the heartbeat, in the native presentation root's DIPs. The editor checks attachment, sequence, native focus and DPI before scrolling. Animated transforms, unsupported visual ancestry, custom effect mapping, separate popup sources and objects without a layout box remain unavailable. This observation does not call BringIntoView or force application layout.

Cross-process native parenting joins input queues. An application input handler can therefore temporarily block editor focus as well as its own view. An independent background watchdog asks the preview dispatcher for a lightweight acknowledgement every 250 ms, with a three-second probe deadline. It separately limits an unfinished keyboard handoff to three seconds, even if a nested dispatcher loop continues acknowledging heartbeats. This bypasses ordinary preview requests, including a stuck property callback. A failed check stops the exact owned process using the pinned launch handle; it does not kill descendant or unrelated processes. A native container is not deliberately destroyed while an attached process's termination remains unconfirmed.

The native send's nominal timeout is not the recovery guarantee: Windows can [ignore it for a shared input queue](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendmessagetimeoutw), and a timeout [cannot cancel a window callback already running](https://devblogs.microsoft.com/oldnewthing/20110915-00/?p=9643). The independent watchdog remains necessary.

The host also ends itself after its editor connection closes, with a bounded background deadline covering a blocked dispatcher or cleanup callback. A stopped or failed preview can be recreated with **Refresh**. This containment does not make arbitrary application constructors, input handlers or property callbacks side-effect-free.

## Validation boundaries

Automated tests cover actual process/pipe authentication, source and compiled presentation-source retention, property state across attachment, native scroll geometry, navigation notifications, owned popup cleanup, stale window leases, preview replacement, cancellation and watchdog recovery. Separate view tests cover the local container's lifecycle and controls. Tests that invoke commands or post messages do not establish natural pointer or keyboard behavior.

Physical pointer and keyboard interaction, IME, visible popup positioning/capture, mixed-monitor DPI transitions, docking/floating and accessibility remain acceptance work. Native HWND content also has airspace restrictions: WPF overlays cannot simply be drawn over the embedded view. This is an initial native interaction implementation, not evidence of complete designer parity.

Synchronous boundary routing and focused-control scrolling still require physical-input validation, including rapid repeated and opposite-direction Tabs. Keyboard routing from nonactivating popups while focus remains in the editor also needs a physical-input check.

See [the full acceptance ledger](xaml-devtools-design.md#full-goal-acceptance-ledger) and [recorded test results](../VALIDATION.md).
