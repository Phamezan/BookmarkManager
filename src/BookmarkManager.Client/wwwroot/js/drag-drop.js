window.BookmarkDragDrop = {
    init: function () {
        document.addEventListener('dragstart', function (e) {
            var el = e.target.closest('[data-drag-type]');
            if (!el) return;

            var dragType = el.getAttribute('data-drag-type');
            var dragId = el.getAttribute('data-drag-id');

            e.dataTransfer.setData('text/plain', dragType + ':' + dragId);
            e.dataTransfer.effectAllowed = 'move';
        });

        document.addEventListener('dragover', function (e) {
            var el = e.target.closest('[data-drop-target]');
            if (!el) return;
            e.preventDefault();
            el.classList.add('drag-over');
        });

        document.addEventListener('dragleave', function (e) {
            var el = e.target.closest('[data-drop-target]');
            if (!el) return;
            el.classList.remove('drag-over');
        });

        document.addEventListener('dragend', function () {
            document.querySelectorAll('[data-drop-target].drag-over').forEach(function (el) {
                el.classList.remove('drag-over');
            });
        });
    }
};

BookmarkDragDrop.init();
