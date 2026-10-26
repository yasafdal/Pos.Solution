// Responsive sidebar controller for POS.Web
window.posSidebar = {
    dotNetRef: null,
    init: function (dotNetRef) {
        this.dotNetRef = dotNetRef;
        var self = this;
        
        window.addEventListener('keydown', function (e) {
            if (e.key === 'Escape' || e.key === 'Esc') {
                if (self.dotNetRef) {
                    self.dotNetRef.invokeMethodAsync('CloseFromJs');
                }
            }
        });

        window.addEventListener('resize', function () {
            if (window.innerWidth >= 1024) {
                if (self.dotNetRef) {
                    self.dotNetRef.invokeMethodAsync('CloseFromJs');
                }
            }
        });
    },
    isDesktop: function () {
        return window.innerWidth >= 1024;
    },
    setBodyScrollLock: function (locked) {
        if (locked && window.innerWidth < 1024) {
            document.body.classList.add('mobile-sidebar-active');
        } else {
            document.body.classList.remove('mobile-sidebar-active');
        }
    },
    dispose: function () {
        this.dotNetRef = null;
        document.body.classList.remove('mobile-sidebar-active');
    }
};
