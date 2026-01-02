// Demo Mode Masking - Masks sensitive strings for screen sharing/recording
// Uses visual overlay for form fields to preserve actual values for API calls
(function () {
    'use strict';

    let mappings = [];
    let isEnabled = false;
    let observer = null;
    let formFieldInterval = null;

    // Load mappings from backend
    async function loadMappings() {
        try {
            const response = await fetch('/api/demo-mappings', { credentials: 'include' });
            if (response.ok) {
                const data = await response.json();
                if (data && data.mappings && data.mappings.length > 0) {
                    mappings = data.mappings;
                    isEnabled = true;
                    return true;
                }
            }
        } catch (e) {
            // Silently fail - demo mode just won't be active
        }
        return false;
    }

    // Apply masking to a string
    function maskString(text) {
        if (!isEnabled || !text) return text;
        let result = text;
        for (const mapping of mappings) {
            // Case-insensitive replacement
            const regex = new RegExp(escapeRegExp(mapping.from), 'gi');
            result = result.replace(regex, mapping.to);
        }
        return result;
    }

    // Escape special regex characters
    function escapeRegExp(string) {
        return string.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    }

    // Check if element should be skipped
    function shouldSkipElement(element) {
        if (!element) return true;
        // Skip script and style elements
        if (element.tagName === 'SCRIPT' || element.tagName === 'STYLE') return true;
        // Skip elements with data-no-mask attribute
        if (element.hasAttribute && element.hasAttribute('data-no-mask')) return true;
        return false;
    }

    // Mask text content of an element
    function maskTextNode(node) {
        if (node.nodeType === Node.TEXT_NODE && node.textContent) {
            const masked = maskString(node.textContent);
            if (masked !== node.textContent) {
                node.textContent = masked;
            }
        }
    }

    // Check if value needs masking
    function needsMasking(value) {
        if (!value) return false;
        for (const mapping of mappings) {
            const regex = new RegExp(escapeRegExp(mapping.from), 'gi');
            if (regex.test(value)) return true;
        }
        return false;
    }

    // Create or update visual overlay for form field
    function maskFormFieldVisual(element) {
        if (element.tagName !== 'INPUT' && element.tagName !== 'TEXTAREA') return;

        const currentValue = element.value;
        if (!currentValue) {
            // Remove overlay if value is empty
            const existingOverlay = element.parentElement?.querySelector('.demo-mask-overlay');
            if (existingOverlay) {
                existingOverlay.textContent = '';
            }
            return;
        }

        // Skip if focused (user is editing)
        if (document.activeElement === element) return;

        // Check if this value needs masking
        if (!needsMasking(currentValue)) {
            // Remove overlay styling if no longer needs masking
            const existingOverlay = element.parentElement?.querySelector('.demo-mask-overlay');
            if (existingOverlay) {
                existingOverlay.textContent = '';
            }
            element.style.color = '';
            return;
        }

        // Ensure parent is positioned
        const parent = element.parentElement;
        if (!parent) return;

        const parentStyle = window.getComputedStyle(parent);
        if (parentStyle.position === 'static') {
            parent.style.position = 'relative';
        }

        // Get input's computed styles for matching
        const inputStyle = window.getComputedStyle(element);

        // Create or get overlay
        let overlay = parent.querySelector('.demo-mask-overlay');
        if (!overlay) {
            overlay = document.createElement('span');
            overlay.className = 'demo-mask-overlay';
            parent.appendChild(overlay);

            // Hide/show overlay on focus/blur
            element.addEventListener('focus', function() {
                overlay.style.display = 'none';
                this.style.color = '';
            });
            element.addEventListener('blur', function() {
                if (needsMasking(this.value)) {
                    overlay.style.display = 'flex';
                    overlay.textContent = maskString(this.value);
                    this.style.color = 'transparent';
                }
            });
        }

        // Apply positioning to match input exactly using offset properties
        overlay.style.position = 'absolute';
        overlay.style.left = element.offsetLeft + 'px';
        overlay.style.top = element.offsetTop + 'px';
        overlay.style.width = element.offsetWidth + 'px';
        overlay.style.height = element.offsetHeight + 'px';
        overlay.style.pointerEvents = 'none';
        overlay.style.display = 'flex';
        overlay.style.alignItems = 'center';
        overlay.style.paddingLeft = inputStyle.paddingLeft;
        overlay.style.paddingRight = inputStyle.paddingRight;
        overlay.style.paddingTop = inputStyle.paddingTop;
        overlay.style.paddingBottom = inputStyle.paddingBottom;
        overlay.style.background = 'transparent';
        overlay.style.color = inputStyle.color;
        overlay.style.fontFamily = inputStyle.fontFamily;
        overlay.style.fontSize = inputStyle.fontSize;
        overlay.style.fontWeight = inputStyle.fontWeight;
        overlay.style.lineHeight = inputStyle.lineHeight;
        overlay.style.letterSpacing = inputStyle.letterSpacing;
        overlay.style.boxSizing = 'border-box';
        overlay.style.overflow = 'hidden';
        overlay.style.whiteSpace = 'nowrap';
        overlay.style.textOverflow = 'ellipsis';
        overlay.style.zIndex = '1';

        // Update overlay text and hide input text
        overlay.textContent = maskString(currentValue);
        element.style.color = 'transparent';
    }

    // Mask select option text (these don't need overlay approach)
    function maskSelectField(element) {
        if (element.tagName !== 'SELECT') return;
        for (const option of element.options) {
            if (!option.dataset.originalText) {
                option.dataset.originalText = option.textContent;
            }
            const masked = maskString(option.dataset.originalText);
            if (masked !== option.textContent) {
                option.textContent = masked;
            }
        }
    }

    // Mask all form fields on the page
    function maskAllFormFields() {
        if (!isEnabled) return;
        const inputs = document.querySelectorAll('input, textarea');
        for (const field of inputs) {
            maskFormFieldVisual(field);
        }
        const selects = document.querySelectorAll('select');
        for (const field of selects) {
            maskSelectField(field);
        }
    }

    // Mask all content in an element tree
    function maskElement(element) {
        if (!isEnabled) return;
        if (shouldSkipElement(element)) return;

        // Walk all text nodes
        const walker = document.createTreeWalker(
            element,
            NodeFilter.SHOW_TEXT,
            null,
            false
        );

        const textNodes = [];
        while (walker.nextNode()) {
            textNodes.push(walker.currentNode);
        }

        for (const node of textNodes) {
            if (!shouldSkipElement(node.parentElement)) {
                maskTextNode(node);
            }
        }

        // Mask form fields with visual overlay
        const inputs = element.querySelectorAll('input, textarea');
        for (const field of inputs) {
            maskFormFieldVisual(field);
        }
        const selects = element.querySelectorAll('select');
        for (const field of selects) {
            maskSelectField(field);
        }
    }

    // Set up MutationObserver to handle dynamic content
    function setupObserver() {
        if (observer) return;

        observer = new MutationObserver((mutations) => {
            for (const mutation of mutations) {
                // Handle added nodes
                if (mutation.type === 'childList') {
                    for (const node of mutation.addedNodes) {
                        if (node.nodeType === Node.ELEMENT_NODE) {
                            // Skip our own overlay elements
                            if (node.classList && node.classList.contains('demo-mask-overlay')) continue;
                            maskElement(node);
                        } else if (node.nodeType === Node.TEXT_NODE) {
                            maskTextNode(node);
                        }
                    }
                }
                // Handle text content changes
                else if (mutation.type === 'characterData') {
                    maskTextNode(mutation.target);
                }
            }
        });

        observer.observe(document.body, {
            childList: true,
            subtree: true,
            characterData: true
        });
    }

    // Start periodic form field checking (for Blazor-set values)
    function startFormFieldPolling() {
        if (formFieldInterval) return;
        // Check form fields every 500ms for new values
        formFieldInterval = setInterval(maskAllFormFields, 500);
    }

    // Initialize demo masking
    async function init() {
        const enabled = await loadMappings();
        if (enabled) {
            // Initial masking of existing content
            maskElement(document.body);
            // Watch for dynamic changes
            setupObserver();
            // Poll for form field value changes (Blazor sets these programmatically)
            startFormFieldPolling();
            console.log('Demo mode active');
        }
    }

    // Start when DOM is ready
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }

    // Also re-run after Blazor enhanced navigation
    if (typeof Blazor !== 'undefined') {
        Blazor.addEventListener('enhancedload', function() {
            if (isEnabled) {
                setTimeout(() => maskElement(document.body), 100);
            }
        });
    }

    // Expose for manual re-masking if needed
    window.DemoMask = {
        refresh: () => {
            maskElement(document.body);
            maskAllFormFields();
        },
        isEnabled: () => isEnabled
    };
})();
