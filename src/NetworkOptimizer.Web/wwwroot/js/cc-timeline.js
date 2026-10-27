// Change Control timeline: every row draws its own slice of a change's dashed line and of the
// incident and gap bands. Each slice's pattern starts at its own top, so the dashes and stripes
// jump where two rows meet. This sets --cc-y on each slice to its offset from the timeline top,
// and the CSS starts the pattern there, so the stacked slices read as one continuous line.

export function attach(timeline) {
    if (!timeline || timeline.__ccAligned) return;
    timeline.__ccAligned = true;
    let queued = false;
    const align = () => {
        queued = false;
        const top = timeline.getBoundingClientRect().top;
        for (const el of timeline.querySelectorAll('.cc-tl-lines'))
            el.style.setProperty('--cc-y', `${el.getBoundingClientRect().top - top}px`);
    };
    const queue = () => {
        if (queued) return;
        queued = true;
        requestAnimationFrame(align);
    };
    // Size changes cover the expand animation and resizes; added rows arrive as child mutations.
    new ResizeObserver(queue).observe(timeline);
    new MutationObserver(queue).observe(timeline, { childList: true, subtree: true });
    queue();
}
