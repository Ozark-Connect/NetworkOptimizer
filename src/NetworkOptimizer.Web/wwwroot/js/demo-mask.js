// Demo Mode Masking - Masks sensitive strings for screen sharing/recording
(function () {
    'use strict';

    let mappings = [];
    let isEnabled = false;
    let observer = null;

    // Load mappings from backend
    async function loadMappings() {
        try {
            const response = await fetch('/api/demo-mappings');
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

    // Mask form field values
    function maskFormField(element) {
        if (element.tagName === 'INPUT' || element.tagName === 'TEXTAREA') {
            // Store original value for form submission
            if (element.value && !element.dataset.originalValue) {
                const masked = maskString(element.value);
                if (masked !== element.value) {
                    element.dataset.originalValue = element.value;
                    element.value = masked;

                    // Restore original on focus, mask on blur
                    if (!element.dataset.maskListenersAdded) {
                        element.addEventListener('focus', function() {
                            if (this.dataset.originalValue) {
                                this.value = this.dataset.originalValue;
                            }
                        });
                        element.addEventListener('blur', function() {
                            if (this.value) {
                                this.dataset.originalValue = this.value;
                                this.value = maskString(this.value);
                            }
                        });
                        element.dataset.maskListenersAdded = 'true';
                    }
                }
            }
        } else if (element.tagName === 'SELECT') {
            // Mask select option text
            for (const option of element.options) {
                const masked = maskString(option.textContent);
                if (masked !== option.textContent) {
                    option.textContent = masked;
                }
            }
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

        // Mask form fields
        const formFields = element.querySelectorAll('input, textarea, select');
        for (const field of formFields) {
            maskFormField(field);
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
                            maskElement(node);
                        } else if (node.nodeType === Node.TEXT_NODE) {
                            maskTextNode(node);
                        }
                    }
                }
                // Handle attribute changes (for form values)
                else if (mutation.type === 'attributes' && mutation.attributeName === 'value') {
                    if (mutation.target.tagName === 'INPUT' || mutation.target.tagName === 'TEXTAREA') {
                        if (!mutation.target.dataset.originalValue) {
                            maskFormField(mutation.target);
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
            characterData: true,
            attributes: true,
            attributeFilter: ['value']
        });
    }

    // Initialize demo masking
    async function init() {
        const enabled = await loadMappings();
        if (enabled) {
            // Initial masking of existing content
            maskElement(document.body);
            // Watch for dynamic changes
            setupObserver();
            console.log('Demo mode active');
        }
    }

    // Start when DOM is ready
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }

    // Also re-run after Blazor renders
    window.addEventListener('load', () => {
        if (isEnabled) {
            setTimeout(() => maskElement(document.body), 100);
        }
    });

    // Expose for manual re-masking if needed
    window.DemoMask = {
        refresh: () => maskElement(document.body),
        isEnabled: () => isEnabled
    };
})();
