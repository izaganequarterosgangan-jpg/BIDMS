/*
 * Certificate Template Manager editor.
 *
 * Two editing surfaces share one value:
 *   - #tplVisual  contenteditable rich-text surface
 *   - #tplSource  raw HTML textarea
 * #tplHtmlInput mirrors the current HTML for form submission, and is also what the
 * live-preview endpoint receives.
 *
 * Placeholders are held as atomic <span contenteditable="false" data-tpl-token="Name">
 * nodes in the visual surface so rich-text commands cannot split them, and are written
 * back out as {{ Name }} when the HTML is serialised.
 *
 * Formatting uses document.execCommand. It is formally deprecated but remains the only
 * zero-dependency way to apply native rich-text commands, and it is used here purely as
 * a client-side editing convenience - the stored value is plain HTML.
 */
(function () {
    'use strict';

    var TPL_ALLOWED_TAGS = [
        'P', 'BR', 'DIV', 'SPAN', 'STRONG', 'B', 'EM', 'I', 'U', 'S', 'STRIKE', 'DEL', 'INS',
        'H1', 'H2', 'H3', 'H4', 'H5', 'H6', 'UL', 'OL', 'LI',
        'TABLE', 'THEAD', 'TBODY', 'TFOOT', 'TR', 'TD', 'TH', 'CAPTION',
        'IMG', 'BLOCKQUOTE', 'HR', 'A', 'FONT', 'SUP', 'SUB', 'CENTER', 'PRE', 'CODE'
    ];

    var TPL_DROP_TAGS = [
        'SCRIPT', 'STYLE', 'IFRAME', 'OBJECT', 'EMBED', 'LINK', 'META',
        'NOSCRIPT', 'FORM', 'INPUT', 'BUTTON', 'BASE', 'APPLET', 'FRAME', 'FRAMESET'
    ];

    var TPL_SEALS = {
        brgy: {
            src: '/images/logos/brgy_governor_boyles_seal.png',
            alt: 'Barangay Seal',
            width: '100px',
            style: 'width: 100px; height: auto; float: left; margin-right: 15px;'
        },
        ubay: {
            src: '/images/logos/ubay_municipality_seal.png',
            alt: 'Municipality Seal',
            width: '100px',
            style: 'width: 100px; height: auto; float: right; margin-left: 15px;'
        }
    };

    // Image resize bounds. Both seals and full-width imported letterheads have to
    // fit, so the range is deliberately wide rather than the 150px cap first
    // proposed. The width is the clamped axis; height follows the intrinsic ratio.
    var TPL_IMG_MIN_W = 20;
    var TPL_IMG_MAX_W = 400;

    // Alignment. Flow modes keep the image in the text flow; the absolute modes
    // (Behind text / In front) lift it out of flow and position it with left/top
    // percentages of the page. Everything is stored as inline style so it survives
    // the paste sanitiser (which drops align="" and data-* but keeps style),
    // SanitizeForStorage, and the #print-sheet pipeline.
    var TPL_LAYOUT_PROPS = ['float', 'display', 'verticalAlign',
        'marginTop', 'marginRight', 'marginBottom', 'marginLeft',
        'position', 'zIndex', 'left', 'top', 'right', 'bottom', 'pointerEvents'];

    function applyImageAlignment(img, align) {
        var style = img.style;
        var wasAbsolute = style.position === 'absolute';
        var prevLeft = style.left;
        var prevTop = style.top;
        var prevOpacity = style.opacity;

        for (var i = 0; i < TPL_LAYOUT_PROPS.length; i++) {
            style[TPL_LAYOUT_PROPS[i]] = '';
        }

        if (align === 'behind' || align === 'front') {
            // position:absolute is relative to .tpl-visual-editor (position:relative),
            // so left/top percentages mean "percent of the page" in the editor, the
            // preview and the printed sheet alike.
            style.position = 'absolute';
            style.pointerEvents = 'auto';

            if (align === 'behind') {
                style.zIndex = '-1';
                style.opacity = prevOpacity || '0.15';
            } else {
                style.zIndex = '10';
                style.opacity = prevOpacity || '1';
            }

            style.left = wasAbsolute && prevLeft ? prevLeft : '5%';
            style.top = wasAbsolute && prevTop ? prevTop : '5%';
            return;
        }

        style.opacity = '';

        if (align === 'left') {
            style.float = 'left';
            style.marginRight = '15px';
            style.marginBottom = '10px';
        } else if (align === 'right') {
            style.float = 'right';
            style.marginLeft = '15px';
            style.marginBottom = '10px';
        } else if (align === 'center') {
            style.display = 'block';
            style.margin = '0 auto 10px auto';
        } else {
            style.display = 'inline-block';
            style.verticalAlign = 'middle';
        }
    }

    function imageAlignment(img) {
        var style = img.style;

        if (style.position === 'absolute') {
            return style.zIndex === '-1' ? 'behind' : 'front';
        }

        if (style.float === 'left') return 'left';
        if (style.float === 'right') return 'right';
        if (style.display === 'block') return 'center';
        if (style.display === 'inline-block' && style.verticalAlign === 'middle') return 'inline';

        return '';
    }

    function isAbsoluteImage(img) {
        return !!img && img.style.position === 'absolute';
    }

    function clamp(value, min, max) {
        return Math.max(min, Math.min(max, value));
    }

    var TPL_TOKEN_PATTERN = /\{\{\s*([A-Za-z][A-Za-z0-9_]*)\s*\}\}/g;

    var visual = document.getElementById('tplVisual');
    var source = document.getElementById('tplSource');
    var htmlInput = document.getElementById('tplHtmlInput');
    var ribbon = document.getElementById('tplRibbon');
    var previewPaper = document.getElementById('tplPreviewPaper');
    var previewScaler = document.getElementById('tplPreviewScaler');
    var previewResident = document.getElementById('tplPreviewResident');
    var previewPurpose = document.getElementById('tplPreviewPurpose');
    var previewDate = document.getElementById('tplPreviewDate');
    var tokenWarning = document.getElementById('tplTokenWarning');
    var tokenWarningText = document.getElementById('tplTokenWarningText');
    var tokenSummary = document.getElementById('tplTokenSummary');
    var importFile = document.getElementById('tplImportFile');
    var importBtn = document.getElementById('tplImportBtn');
    var modalPaper = document.getElementById('tplModalPaper');
    var renderTimer = null;
    var mode = 'visual';

    // ------------------------------------------------------------- value plumbing

    function currentHtml() {
        return mode === 'visual' ? serialiseVisual() : source.value;
    }

    function setHtml(html) {
        deselectImage();
        source.value = html;
        visual.innerHTML = protectTokens(html);
        syncHiddenInput();
    }

    function syncHiddenInput() {
        htmlInput.value = currentHtml();
    }

    function serialiseVisual() {
        var clone = visual.cloneNode(true);
        stripEditorUi(clone);
        unwrapTokens(clone);
        stripEditingArtifacts(clone);
        return clone.innerHTML;
    }

    /* Selection outlines, resize grips and the floating toolbar are editor chrome.
       They must never reach App_Data/certificate_templates.json or the print sheet.
       The overlay normally lives outside #tplVisual, so this is a second line of
       defence against it ever being saved. */
    function stripEditorUi(root) {
        var ui = root.querySelectorAll('.editor-handle-ui, [data-editor-ui]');
        for (var i = ui.length - 1; i >= 0; i--) {
            ui[i].remove();
        }

        var marked = root.querySelectorAll('.tpl-img-drop-target');
        for (var j = 0; j < marked.length; j++) {
            marked[j].classList.remove('tpl-img-drop-target');
        }
    }

    function protectTokens(html) {
        return String(html).replace(TPL_TOKEN_PATTERN, function (match, name) {
            return '<span class="tpl-ph" contenteditable="false" data-tpl-token="' + name + '">' +
                '{{ ' + name + ' }}</span>';
        });
    }

    function unwrapTokens(root) {
        var spans = root.querySelectorAll('span[data-tpl-token]');
        for (var i = 0; i < spans.length; i++) {
            var name = spans[i].getAttribute('data-tpl-token');
            spans[i].replaceWith(document.createTextNode('{{ ' + name + ' }}'));
        }
    }

    /* contenteditable leaves behind stray empty block elements and <br> filler at the
       end of the document; those would otherwise show up as phantom blank lines. */
    function stripEditingArtifacts(root) {
        var empties = root.querySelectorAll('div:empty, p:empty, span:empty, br');
        for (var i = empties.length - 1; i >= 0; i--) {
            var el = empties[i];
            if (el.tagName === 'BR' && el.nextSibling) continue;
            el.remove();
        }

        while (visual && root.lastChild && root.lastChild.nodeType === 3 && !root.lastChild.nodeValue.trim()) {
            root.lastChild.remove();
        }
    }

    // ------------------------------------------------------------- sanitising

    function sanitize(html) {
        var parsed = new DOMParser().parseFromString('<body>' + html + '</body>', 'text/html');
        cleanNode(parsed.body);
        return parsed.body.innerHTML;
    }

    function cleanNode(node) {
        var children = Array.prototype.slice.call(node.childNodes);

        for (var i = 0; i < children.length; i++) {
            var el = children[i];

            if (el.nodeType === 3) continue;

            if (el.nodeType !== 1) {
                el.remove();
                continue;
            }

            var tag = el.tagName.toUpperCase();

            if (TPL_DROP_TAGS.indexOf(tag) !== -1) {
                el.remove();
                continue;
            }

            cleanAttributes(el, tag);
            cleanNode(el);

            if (TPL_ALLOWED_TAGS.indexOf(tag) === -1) {
                // Keep the text, drop the wrapper.
                el.replaceWith.apply(el, Array.prototype.slice.call(el.childNodes));
            }
        }
    }

    function cleanAttributes(el, tag) {
        var attributes = Array.prototype.slice.call(el.attributes);

        for (var i = 0; i < attributes.length; i++) {
            var name = attributes[i].name.toLowerCase();
            var value = attributes[i].value || '';
            var drop = false;

            if (name.indexOf('on') === 0) {
                // Keep only the seal-loading fallbacks on images.
                drop = !(tag === 'IMG' && (name === 'onerror' || name === 'onload'));
            } else if (name === 'style' || name === 'href' || name === 'src' || name === 'alt'
                || name === 'width' || name === 'height' || name === 'class' || name === 'id'
                || name === 'colspan' || name === 'rowspan' || name === 'title' || name === 'dir') {
                drop = !isSafeUrl(value);
            } else {
                drop = true;
            }

            if (drop) {
                el.removeAttribute(attributes[i].name);
            }
        }
    }

    function isSafeUrl(value) {
        var trimmed = String(value).trim().replace(/[\s\x00-\x1f]/g, '').toLowerCase();

        if (trimmed.indexOf('javascript:') === 0 || trimmed.indexOf('vbscript:') === 0) {
            return false;
        }

        if (trimmed.indexOf('data:') === 0) {
            // Imported .docx letterheads embed logos as data URIs.
            return trimmed.indexOf('data:image/') === 0;
        }

        return true;
    }

    // ------------------------------------------------------------- mode switching

    function setMode(next) {
        if (next === mode) return;

        if (mode === 'visual') {
            source.value = serialiseVisual();
        } else {
            visual.innerHTML = protectTokens(source.value);
        }

        mode = next;
        deselectImage();

        var buttons = document.querySelectorAll('.tpl-mode-btn');
        for (var i = 0; i < buttons.length; i++) {
            buttons[i].classList.toggle('active', buttons[i].getAttribute('data-mode') === mode);
        }

        source.style.display = mode === 'source' ? 'block' : 'none';
        visual.style.display = mode === 'visual' ? 'block' : 'none';
        ribbon.style.display = mode === 'visual' ? 'flex' : 'none';

        if (mode === 'source') {
            source.focus();
        } else {
            visual.focus();
        }

        syncHiddenInput();
    }

    // ------------------------------------------------------------- rich text

    function exec(command) {
        visual.focus();
        document.execCommand(command, false, null);
        syncHiddenInput();
        schedulePreview();
    }

    function execBlock(tag) {
        visual.focus();
        document.execCommand('formatBlock', false, '<' + tag + '>');
        syncHiddenInput();
        schedulePreview();
    }

    function insertHtml(html) {
        visual.focus();
        document.execCommand('insertHTML', false, html);
        syncHiddenInput();
        schedulePreview();
    }

    function insertToken(placeholder) {
        if (mode === 'source') {
            var start = source.selectionStart;
            var end = source.selectionEnd;
            var value = source.value;

            source.value = value.slice(0, start) + placeholder + value.slice(end);
            source.selectionStart = source.selectionEnd = start + placeholder.length;
            source.focus();
        } else {
            insertHtml(protectTokens(placeholder));
        }

        syncHiddenInput();
        schedulePreview();
    }

    function insertImage(opts) {
        var image = document.createElement('img');
        image.setAttribute('src', opts.src);
        image.setAttribute('alt', opts.alt);
        image.setAttribute('style', opts.style ||
            ('width: ' + (opts.width || '120px') + '; height: auto; object-fit: contain;'));
        if (opts.onError !== false) {
            image.setAttribute('onerror', "this.style.display='none';");
        }

        insertHtml(image.outerHTML);
        selectNewestImage();
    }

    // ------------------------------------------------------ image drag / resize
    //
    // Option A: alignment stays in flow (float / block-centre / inline-block) so it
    // round-trips through the paste sanitiser, SanitizeForStorage and #print-sheet,
    // while the handles give a free move-and-resize feel. The overlay and toolbar are
    // siblings of #tplVisual (children of .tpl-editor-shell) and tagged
    // .editor-handle-ui, so serialiseVisual() can never persist them.

    var editorShell = null;
    var imageOverlay = null;
    var imageToolbar = null;
    var selectedImage = null;
    var dragState = null;

    function buildImageUi() {
        editorShell = visual && visual.closest ? visual.closest('.tpl-editor-shell') : null;
        if (!editorShell || imageOverlay) return;

        imageOverlay = document.createElement('div');
        imageOverlay.className = 'tpl-img-overlay editor-handle-ui';
        imageOverlay.setAttribute('data-editor-ui', '1');
        imageOverlay.setAttribute('aria-hidden', 'true');

        var corners = ['nw', 'ne', 'se', 'sw'];
        for (var i = 0; i < corners.length; i++) {
            var handle = document.createElement('div');
            handle.className = 'tpl-img-handle editor-handle-ui';
            handle.setAttribute('data-editor-ui', '1');
            handle.setAttribute('data-corner', corners[i]);
            handle.setAttribute('title', 'Drag to resize');
            imageOverlay.appendChild(handle);
        }

        imageToolbar = document.createElement('div');
        imageToolbar.className = 'tpl-img-toolbar editor-handle-ui';
        imageToolbar.setAttribute('data-editor-ui', '1');
        imageToolbar.setAttribute('role', 'toolbar');
        imageToolbar.setAttribute('aria-label', 'Image alignment');

        var tools = [
            ['left', 'Float left', 'bi bi-text-left'],
            ['center', 'Centre', 'bi bi-text-center'],
            ['right', 'Float right', 'bi bi-text-right'],
            ['inline', 'Inline / default', 'bi bi-text-paragraph'],
            ['sep'],
            ['behind', 'Behind text (watermark)', 'bi bi-back'],
            ['front', 'In front / free move', 'bi bi-front'],
            ['sep'],
            ['opacity', 'Opacity', null]
        ];

        for (var t = 0; t < tools.length; t++) {
            if (tools[t][0] === 'sep') {
                var sep = document.createElement('span');
                sep.className = 'tpl-img-tool-sep';
                imageToolbar.appendChild(sep);
            } else if (tools[t][0] === 'opacity') {
                var opacity = document.createElement('input');
                opacity.type = 'range';
                opacity.className = 'tpl-img-opacity';
                opacity.min = '0.05';
                opacity.max = '1';
                opacity.step = '0.05';
                opacity.value = '1';
                opacity.title = 'Opacity';
                opacity.setAttribute('data-editor-ui', '1');
                opacity.addEventListener('input', onOpacityInput);
                opacity.addEventListener('change', commitImageChange);
                imageToolbar.appendChild(opacity);
            } else {
                var btn = document.createElement('button');
                btn.type = 'button';
                btn.className = 'tpl-img-tool';
                btn.setAttribute('data-align', tools[t][0]);
                btn.setAttribute('title', tools[t][1]);
                btn.innerHTML = '<i class="' + tools[t][2] + '"></i>';
                imageToolbar.appendChild(btn);
            }
        }

        var sizeLabel = document.createElement('span');
        sizeLabel.className = 'tpl-img-size';
        imageToolbar.appendChild(sizeLabel);

        editorShell.appendChild(imageOverlay);
        editorShell.appendChild(imageToolbar);

        imageOverlay.addEventListener('mousedown', onOverlayMouseDown);
        imageToolbar.addEventListener('mousedown', function (event) {
            // Keep the caret in the editor for button clicks, but let the opacity
            // slider receive its own drag gestures.
            if (event.target.closest('.tpl-img-tool')) {
                event.preventDefault();
            }
        });
        imageToolbar.addEventListener('click', onToolbarClick);

        hideImageUi();
    }

    function hideImageUi() {
        if (imageOverlay) imageOverlay.style.display = 'none';
        if (imageToolbar) imageToolbar.style.display = 'none';
    }

    function selectImage(img) {
        if (selectedImage === img) {
            syncImageUi();
            return;
        }

        deselectImage();
        selectedImage = img;
        buildImageUi();

        if (imageOverlay) imageOverlay.style.display = 'block';
        if (imageToolbar) imageToolbar.style.display = 'flex';

        if (!img.complete) {
            img.addEventListener('load', syncImageUi, { once: true });
            img.addEventListener('error', syncImageUi, { once: true });
        }

        syncImageUi();
        refreshToolbarState();
    }

    function deselectImage() {
        selectedImage = null;
        dragState = null;
        if (visual) clearDropTarget();
        hideImageUi();
    }

    function syncImageUi() {
        if (!selectedImage || !imageOverlay || !editorShell) return;
        if (!editorShell.contains(selectedImage)) {
            deselectImage();
            return;
        }

        var r = selectedImage.getBoundingClientRect();
        var s = editorShell.getBoundingClientRect();
        var top = r.top - s.top;
        var left = r.left - s.left;

        // An absolutely positioned image sits under the text (z-index:-1), so clicks
        // would land on the text instead. The overlay rectangle takes the drag
        // instead whenever the image is out of flow.
        imageOverlay.classList.toggle('is-absolute', isAbsoluteImage(selectedImage));

        imageOverlay.style.top = top + 'px';
        imageOverlay.style.left = left + 'px';
        imageOverlay.style.width = r.width + 'px';
        imageOverlay.style.height = r.height + 'px';

        var sizeLabel = imageToolbar ? imageToolbar.querySelector('.tpl-img-size') : null;
        if (sizeLabel) sizeLabel.textContent = Math.round(r.width) + 'px';

        var barW = imageToolbar ? (imageToolbar.offsetWidth || 200) : 200;
        var barH = imageToolbar ? (imageToolbar.offsetHeight || 34) : 34;
        var barTop = top - barH - 6;
        if (barTop < 2) barTop = top + r.height + 6;

        imageToolbar.style.top =
            clamp(barTop, 2, Math.max(2, editorShell.clientHeight - barH - 2)) + 'px';
        imageToolbar.style.left =
            clamp(left, 2, Math.max(2, editorShell.clientWidth - barW - 2)) + 'px';
    }

    function refreshToolbarState() {
        if (!imageToolbar || !selectedImage) return;
        var active = imageAlignment(selectedImage);
        var buttons = imageToolbar.querySelectorAll('.tpl-img-tool');
        for (var i = 0; i < buttons.length; i++) {
            buttons[i].classList.toggle('is-active', buttons[i].getAttribute('data-align') === active);
        }

        var slider = imageToolbar.querySelector('.tpl-img-opacity');
        if (slider) {
            var current = parseFloat(selectedImage.style.opacity);
            slider.value = isNaN(current) ? '1' : String(current);
        }
    }

    function onOpacityInput(event) {
        if (!selectedImage) return;
        var value = clamp(parseFloat(event.target.value) || 1, 0.05, 1);
        selectedImage.style.opacity = String(value);
        syncImageUi();
    }

    function onOverlayMouseDown(event) {
        if (!selectedImage) return;

        var handle = event.target.closest ? event.target.closest('.tpl-img-handle') : null;

        event.preventDefault();
        event.stopPropagation();

        if (handle) {
            dragState = {
                kind: 'resize',
                corner: handle.getAttribute('data-corner'),
                startX: event.clientX,
                startW: selectedImage.getBoundingClientRect().width
            };
        } else if (isAbsoluteImage(selectedImage)) {
            dragState = moveDragState(event);
        } else {
            return;
        }

        document.addEventListener('mousemove', onDocMouseMove);
        document.addEventListener('mouseup', onDocMouseUp);
    }

    function moveDragState(event) {
        var rect = selectedImage.getBoundingClientRect();

        return {
            kind: 'move',
            absolute: isAbsoluteImage(selectedImage),
            startX: event.clientX,
            startY: event.clientY,
            moved: false,
            // Preserve the grab point so the image does not jump to the cursor.
            offsetX: event.clientX - rect.left,
            offsetY: event.clientY - rect.top
        };
    }

    function onToolbarClick(event) {
        var tool = event.target.closest ? event.target.closest('.tpl-img-tool') : null;
        if (!tool || !selectedImage) return;

        event.preventDefault();
        applyImageAlignment(selectedImage, tool.getAttribute('data-align'));
        refreshToolbarState();
        syncImageUi();
        commitImageChange();
    }

    function onVisualMouseDown(event) {
        var img = event.target.closest ? event.target.closest('img') : null;
        if (!img || !visual.contains(img)) return;

        // Suppress the native image-drag ghost; dragging is handled here.
        event.preventDefault();
        selectImage(img);

        dragState = moveDragState(event);
        document.addEventListener('mousemove', onDocMouseMove);
        document.addEventListener('mouseup', onDocMouseUp);
    }

    function onDocMouseMove(event) {
        if (!dragState || !selectedImage) return;

        if (dragState.kind === 'resize') {
            var dx = event.clientX - dragState.startX;
            var grows = dragState.corner === 'ne' || dragState.corner === 'se';
            setImageWidth(selectedImage, grows ? dragState.startW + dx : dragState.startW - dx);
            syncImageUi();
            return;
        }

        if (Math.abs(event.clientX - dragState.startX) > 3 ||
            Math.abs(event.clientY - dragState.startY) > 3) {
            dragState.moved = true;
        }
        if (!dragState.moved) return;

        if (dragState.absolute) {
            positionAbsoluteImage(event);
            syncImageUi();
            return;
        }

        applyImageAlignment(selectedImage, horizontalZone(event.clientX));
        repositionVertically(event);
        refreshToolbarState();
        syncImageUi();
    }

    /* Writes the pointer position as a percentage of the page (.tpl-visual-editor).
       Percentages are stored rather than pixels so the placement holds in the
       preview and on the printed A4 sheet, which are all a different size. */
    function positionAbsoluteImage(event) {
        var page = visual;
        if (!page) return;

        var r = page.getBoundingClientRect();
        if (!r.width || !r.height) return;

        var x = ((event.clientX - r.left - dragState.offsetX) / r.width) * 100;
        var y = ((event.clientY - r.top - dragState.offsetY) / r.height) * 100;

        selectedImage.style.left = clamp(x, 0, 100).toFixed(2) + '%';
        selectedImage.style.top = clamp(y, 0, 100).toFixed(2) + '%';
    }

    function onDocMouseUp() {
        document.removeEventListener('mousemove', onDocMouseMove);
        document.removeEventListener('mouseup', onDocMouseUp);
        clearDropTarget();

        if (dragState && (dragState.kind === 'resize' || dragState.moved)) {
            commitImageChange();
        }
        dragState = null;
    }

    function setImageWidth(img, width) {
        width = clamp(Math.round(width), TPL_IMG_MIN_W, TPL_IMG_MAX_W);
        img.style.width = width + 'px';
        img.style.height = 'auto';
        // Any width/height attribute would override the inline style.
        img.removeAttribute('width');
        img.removeAttribute('height');
    }

    function horizontalZone(clientX) {
        var r = visual.getBoundingClientRect();
        var ratio = r.width > 0 ? (clientX - r.left) / r.width : 0.5;
        if (ratio < 0.34) return 'left';
        if (ratio > 0.66) return 'right';
        return 'center';
    }

    function topLevelBlock(node) {
        var el = node;
        while (el && el.parentNode !== visual) {
            el = el.parentNode;
        }
        return el && el.parentNode === visual ? el : null;
    }

    function repositionVertically(event) {
        var block = topLevelBlock(selectedImage);
        // A table is a layout container; never lift it out of the document flow.
        if (!block || block.tagName === 'TABLE') return;

        var children = Array.prototype.slice.call(visual.children);
        for (var i = 0; i < children.length; i++) {
            var sibling = children[i];
            if (sibling === block) continue;

            var r = sibling.getBoundingClientRect();
            if (event.clientY < r.top || event.clientY > r.bottom) continue;

            var before = event.clientY < r.top + r.height / 2;
            var anchor = before ? sibling : sibling.nextSibling;
            if (anchor !== block && block.nextSibling !== anchor) {
                visual.insertBefore(block, anchor);
            }
            setDropTarget(sibling);
            return;
        }

        clearDropTarget();
    }

    function setDropTarget(el) {
        clearDropTarget();
        if (el) el.classList.add('tpl-img-drop-target');
    }

    function clearDropTarget() {
        if (!visual) return;
        var marked = visual.querySelectorAll('.tpl-img-drop-target');
        for (var i = 0; i < marked.length; i++) {
            marked[i].classList.remove('tpl-img-drop-target');
        }
    }

    function commitImageChange() {
        syncHiddenInput();
        schedulePreview();
    }

    function onDocumentMouseDown(event) {
        if (!selectedImage) return;
        if (event.target === selectedImage) return;
        if (imageOverlay && imageOverlay.contains(event.target)) return;
        if (imageToolbar && imageToolbar.contains(event.target)) return;
        deselectImage();
    }

    function onDocumentKeyDown(event) {
        if (event.key === 'Escape') deselectImage();
    }

    function selectNewestImage() {
        var images = visual.querySelectorAll('img');
        if (images.length) selectImage(images[images.length - 1]);
    }

    /* ---- Spacing and type-size controls ---------------------------------- */

    // Opening a <select> moves focus out of the editor, which can collapse the
    // document selection. The range is recorded while the user types and clicks, so
    // the toolbar always knows which block it was aimed at.
    var savedRange = null;

    function rememberSelection() {
        if (!visual) return;

        var sel = window.getSelection();
        if (!sel || sel.rangeCount === 0) return;

        var range = sel.getRangeAt(0);
        if (!visual.contains(range.commonAncestorContainer)) return;

        savedRange = range.cloneRange();
    }

    function ensureSelection() {
        var sel = window.getSelection();

        if (sel && sel.rangeCount > 0 && visual.contains(sel.getRangeAt(0).commonAncestorContainer)) {
            return;
        }

        if (!savedRange) return;

        sel.removeAllRanges();
        sel.addRange(savedRange);
    }

    var TPL_BLOCK_TAGS = {
        P: 1, DIV: 1, H1: 1, H2: 1, H3: 1, H4: 1, H5: 1, H6: 1,
        LI: 1, BLOCKQUOTE: 1, TD: 1, TH: 1, TABLE: 1, UL: 1, OL: 1
    };

    function closestBlock(node) {
        var element = node && node.nodeType === 3 ? node.parentElement : node;

        while (element && element !== visual) {
            // The certificate wrapper is the whole page, never the target of a spacing
            // change, so keep walking up past it to the paragraph inside.
            if (TPL_BLOCK_TAGS[element.tagName]
                && !(element.classList && element.classList.contains('certificate-container'))) {
                return element;
            }

            element = element.parentElement;
        }

        return null;
    }

    // Every block the current selection touches, so one change covers them all.
    function selectedBlocks() {
        var sel = window.getSelection();
        if (!sel || sel.rangeCount === 0) return [];

        var blocks = [];

        for (var i = 0; i < sel.rangeCount; i++) {
            var range = sel.getRangeAt(i);
            var candidates = [closestBlock(range.startContainer), closestBlock(range.endContainer)];

            for (var c = 0; c < candidates.length; c++) {
                if (candidates[c] && blocks.indexOf(candidates[c]) === -1) {
                    blocks.push(candidates[c]);
                }
            }
        }

        return blocks;
    }

    // Wraps the selection in a span carrying one declaration. The existing nodes are
    // moved rather than re-serialised, so bold, italic and links inside the selection
    // survive, and what is stored is real markup the preview and printer both render.
    function wrapSelectionWithStyle(declaration) {
        visual.focus();
        ensureSelection();

        var sel = window.getSelection();
        if (!sel || sel.rangeCount === 0) return false;

        var range = sel.getRangeAt(0);

        if (range.collapsed) {
            // Nothing selected: an empty carrier gives the next character the style.
            // serialiseVisual() drops empty elements, so it never reaches the saved
            // template on its own.
            insertHtml('<span style="' + declaration + '"></span>');
            return true;
        }

        var span = document.createElement('span');
        span.setAttribute('style', declaration);

        try {
            span.appendChild(range.extractContents());
            range.insertNode(span);
        } catch (e) {
            return false;
        }

        sel.removeAllRanges();

        var restored = document.createRange();
        restored.selectNodeContents(span);
        sel.addRange(restored);

        syncHiddenInput();
        schedulePreview();
        return true;
    }

    // Writes one declaration onto the inline style of every selected block, which is
    // exactly how an imported .docx already stores its spacing.
    function applyBlockStyle(property, value) {
        visual.focus();
        ensureSelection();

        var blocks = selectedBlocks();
        if (blocks.length === 0) return false;

        for (var i = 0; i < blocks.length; i++) {
            blocks[i].style[property] = value;
        }

        syncHiddenInput();
        schedulePreview();
        return true;
    }

    // Steps a length up or down on the selected blocks, never below zero.
    function stepBlockStyle(property, step) {
        visual.focus();
        ensureSelection();

        var blocks = selectedBlocks();
        if (blocks.length === 0) return false;

        for (var i = 0; i < blocks.length; i++) {
            var current = parseFloat(blocks[i].style[property]);
            if (!isFinite(current)) current = 0;

            blocks[i].style[property] = Math.max(0, Math.round((current + step) * 100) / 100) + 'px';
        }

        syncHiddenInput();
        schedulePreview();
        return true;
    }

    function onRibbonChange(event) {
        var field = event.target;

        if (field.id === 'tplLineHeight' && field.value) {
            applyBlockStyle('line-height', field.value);
        } else if (field.id === 'tplFontSize' && field.value) {
            wrapSelectionWithStyle('font-size: ' + field.value);
        }
    }

    function onRibbonClick(event) {
        var button = event.target.closest('.tpl-rbtn');
        if (!button) return;

        var command = button.getAttribute('data-cmd');
        var block = button.getAttribute('data-block');
        var image = button.getAttribute('data-image');
        var spacing = button.getAttribute('data-spacing');

        if (command) {
            exec(command);
        } else if (block) {
            execBlock(block);
        } else if (spacing === 'space-up') {
            stepBlockStyle('margin-bottom', 4);
        } else if (spacing === 'space-down') {
            stepBlockStyle('margin-bottom', -4);
        } else if (spacing === 'indent-in') {
            stepBlockStyle('margin-left', 18);
        } else if (spacing === 'indent-out') {
            stepBlockStyle('margin-left', -18);
        } else if (image === 'brgy' || image === 'ubay') {
            insertImage(TPL_SEALS[image]);
        } else if (image === 'url') {
            var url = window.prompt('Image URL (https://… or /images/…):', '');
            if (url && url.trim()) {
                insertImage({ src: url.trim(), alt: 'image', width: '120px' });
            }
        }
    }

    function onPaste(event) {
        var clipboard = event.clipboardData;
        if (!clipboard) return;

        event.preventDefault();

        var html = clipboard.getData('text/html');
        var text = clipboard.getData('text/plain');

        if (html) {
            insertHtml(sanitize(html));
        } else if (text) {
            insertHtml(text.replace(/[&<>]/g, function (c) {
                return { '&': '&amp;', '<': '&lt;', '>': '&gt;' }[c];
            }).replace(/\n{2,}/g, '<br /><br />').replace(/\n/g, '<br />'));
        }
    }

    function onVisualDrop(event) {
        // Images dropped from a desktop are not reachable on the server, so they are
        // refused rather than silently producing a broken <img>.
        if (event.dataTransfer && Array.prototype.indexOf.call(event.dataTransfer.types || [], 'Files') !== -1) {
            event.preventDefault();
            window.alert('Drag-and-drop image files is not supported. Use Insert Seal, or import a .docx/.html file that already embeds the logo.');
        }
    }

    // ------------------------------------------------------------- preview

    function antiforgeryToken() {
        var meta = document.querySelector('meta[name="csrf-token"]');
        return meta ? meta.getAttribute('content') : '';
    }

    function schedulePreview() {
        syncHiddenInput();
        refreshTokenSummary();
        window.clearTimeout(renderTimer);
        renderTimer = window.setTimeout(renderPreview, 400);
    }

    /* The preview sheet is laid out at its real A4 width and then scaled to fit the
       panel, so it wraps exactly where the editor canvas and the print sheet wrap.
       Only the scale is recomputed here: nothing inside the paper is touched, so
       alignment, spacing and the resolved placeholder text stay as rendered. */
    function fitPreview() {
        if (!previewScaler || !previewPaper) return;

        var stage = previewScaler.parentElement;
        if (!stage) return;

        var stageStyle = window.getComputedStyle(stage);
        var available = stage.clientWidth
            - parseFloat(stageStyle.paddingLeft)
            - parseFloat(stageStyle.paddingRight);

        // offsetWidth/offsetHeight ignore the transform, so these report the true
        // page size even while a scale is already applied.
        var paperWidth = previewPaper.offsetWidth;
        var paperHeight = previewPaper.offsetHeight;

        if (!paperWidth || !paperHeight || !(available > 0)) return;

        var scale = clamp(available / paperWidth, 0.3, 1);

        previewPaper.style.setProperty('--tpl-preview-scale', scale);
        previewScaler.style.width = (paperWidth * scale) + 'px';
        previewScaler.style.height = (paperHeight * scale) + 'px';
    }

    function renderPreview() {
        var body = new URLSearchParams();
        body.append('templateHtml', currentHtml());
        body.append('residentId', previewResident ? previewResident.value : '');
        body.append('purpose', previewPurpose ? previewPurpose.value : '');
        body.append('issueDate', previewDate ? previewDate.value : '');

        previewPaper.setAttribute('aria-busy', 'true');

        fetch('/Home/PreviewCertificateTemplate', {
            method: 'POST',
            headers: {
                'Content-Type': 'application/x-www-form-urlencoded; charset=UTF-8',
                'RequestVerificationToken': antiforgeryToken()
            },
            body: body.toString()
        })
            .then(function (r) {
                if (!r.ok) throw new Error('render failed');
                return r.json();
            })
            .then(function (data) {
                previewPaper.innerHTML = data.html || '';
                fitPreview();
                previewPaper.removeAttribute('aria-busy');
                showTokenWarning(data.unknownTokens || []);
            })
            .catch(function () {
                previewPaper.removeAttribute('aria-busy');
                previewPaper.innerHTML =
                    '<p class="tpl-preview-error">Preview is unavailable right now. The template still saves and prints normally.</p>';
            });
    }

    function showTokenWarning(unknown) {
        if (!tokenWarning) return;

        if (unknown.length > 0) {
            tokenWarningText.textContent =
                'Unrecognised placeholder' + (unknown.length === 1 ? '' : 's') + ': ' +
                unknown.map(function (t) { return '{{ ' + t + ' }}'; }).join(', ') +
                '. These will print exactly as typed.';
            tokenWarning.style.display = 'block';
        } else {
            tokenWarning.style.display = 'none';
        }
    }

    function refreshTokenSummary() {
        if (!tokenSummary) return;

        var pattern = new RegExp(TPL_TOKEN_PATTERN.source, 'g');
        var used = [];
        var match;

        while ((match = pattern.exec(currentHtml())) !== null) {
            if (used.indexOf(match[1]) === -1) used.push(match[1]);
        }

        if (used.length === 0) {
            tokenSummary.textContent = 'No placeholders used yet';
            tokenSummary.className = 'tpl-token-summary tpl-token-summary-empty';
            return;
        }

        tokenSummary.textContent = used.length + ' placeholder' + (used.length === 1 ? '' : 's') + ' used';
        tokenSummary.className = 'tpl-token-summary';
    }

    function openPreviewModal() {
        var body = new URLSearchParams();
        body.append('templateHtml', currentHtml());
        body.append('residentId', previewResident ? previewResident.value : '');
        body.append('purpose', previewPurpose ? previewPurpose.value : '');
        body.append('issueDate', previewDate ? previewDate.value : '');

        modalPaper.innerHTML = '<p class="tpl-preview-error">Rendering preview…</p>';

        fetch('/Home/PreviewCertificateTemplate', {
            method: 'POST',
            headers: {
                'Content-Type': 'application/x-www-form-urlencoded; charset=UTF-8',
                'RequestVerificationToken': antiforgeryToken()
            },
            body: body.toString()
        })
            .then(function (r) {
                if (!r.ok) throw new Error('render failed');
                return r.json();
            })
            .then(function (data) {
                modalPaper.innerHTML = data.html || '';

                if (window.bootstrap && bootstrap.Modal) {
                    new bootstrap.Modal(document.getElementById('tplPreviewModal')).show();
                } else {
                    document.getElementById('tplPreviewModal').style.display = 'block';
                }
            })
            .catch(function () {
                modalPaper.innerHTML =
                    '<p class="tpl-preview-error">Preview is unavailable right now. The template still saves and prints normally.</p>';
                if (window.bootstrap && bootstrap.Modal) {
                    new bootstrap.Modal(document.getElementById('tplPreviewModal')).show();
                }
            });
    }

    function printModalPreview() {
        if (window.BdimsCertificates && BdimsCertificates.printHtml) {
            BdimsCertificates.printHtml(modalPaper.innerHTML, 'Certificate Preview');
            return;
        }

        window.print();
    }

    // ------------------------------------------------------------- wiring

    window.tplSetMode = setMode;
    window.tplInsertToken = insertToken;
    window.tplOpenPreviewModal = openPreviewModal;
    window.tplPrintModalPreview = printModalPreview;

    window.tplRestoreDefault = function () {
        if (!confirm('Replace the current layout with the default template? Unsaved changes will be lost.')) {
            return;
        }

        var typeSelect = document.getElementById('tplTypeSelect');
        var hasSavedLayout = parseInt(document.getElementById('tplId').value, 10) !== 0;

        setHtml(hasSavedLayout || !typeSelect
            ? document.getElementById('tplDefaultHtml').value
            : document.getElementById('tplDefaultSkeleton').value);

        schedulePreview();
        visual.scrollTop = 0;
    };

    window.confirmImport = function () {
        if (!importFile || !importFile.files || importFile.files.length === 0) {
            alert('Choose a .docx or .html file first.');
            return false;
        }

        return 'Importing replaces the saved layout for this certificate type with the uploaded file.\n\n' +
            'Word documents are converted best-effort: columns, floating text boxes and headers/footers are dropped.\n\nContinue?';
    };

    // These are wired as `onsubmit="return fn(this)"`, so they must return a boolean.
    // They previously returned a message string, which is always truthy, so the
    // confirmation dialog never appeared and the form submitted unconditionally.
    window.confirmNewType = function () {
        return window.confirm('Create this certificate type? A fee row and a starter template are added together.');
    };

    window.confirmDeleteTemplate = function (form) {
        var name = (form && form.getAttribute('data-template-name')) || 'this template';
        return window.confirm('Delete the template for "' + name + '"? Certificates already issued keep their own copy of the layout.');
    };

    document.addEventListener('DOMContentLoaded', function () {
        if (!visual || !source || !htmlInput) return;

        setHtml(document.getElementById('tplInitialHtml').value);

        if (ribbon) {
            // Keeping focus (and therefore the caret) inside the editor requires
            // suppressing the toolbar's default mousedown focus shift.
            ribbon.addEventListener('mousedown', function (event) {
                if (event.target.closest('.tpl-rbtn')) {
                    event.preventDefault();
                }
            });
            ribbon.addEventListener('click', onRibbonClick);
            ribbon.addEventListener('change', onRibbonChange);
        }

        visual.addEventListener('input', schedulePreview);
        visual.addEventListener('keyup', rememberSelection);
        visual.addEventListener('mouseup', rememberSelection);
        visual.addEventListener('paste', onPaste);
        visual.addEventListener('drop', onVisualDrop);
        visual.addEventListener('mousedown', onVisualMouseDown);
        visual.addEventListener('scroll', syncImageUi, { passive: true });
        source.addEventListener('input', schedulePreview);

        buildImageUi();
        document.addEventListener('mousedown', onDocumentMouseDown);
        document.addEventListener('keydown', onDocumentKeyDown);
        window.addEventListener('resize', syncImageUi);
        window.addEventListener('resize', fitPreview);
        fitPreview();

        if (previewResident) previewResident.addEventListener('change', renderPreview);
        if (previewPurpose) previewPurpose.addEventListener('input', schedulePreview);
        if (previewDate) previewDate.addEventListener('change', renderPreview);

        if (importFile) {
            importFile.addEventListener('change', function () {
                if (importBtn) {
                    importBtn.disabled = importFile.files.length === 0;
                }
            });
        }

        var form = document.getElementById('tplForm');
        if (form) {
            form.addEventListener('submit', syncHiddenInput);
        }

        refreshTokenSummary();
    });
})();
