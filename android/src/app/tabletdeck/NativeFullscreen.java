package app.tabletdeck;

import android.app.Activity;
import android.os.Build;
import android.view.View;
import android.view.Window;
import android.view.WindowInsets;
import android.view.WindowInsetsController;
import android.view.WindowManager;
import android.window.OnBackInvokedCallback;
import android.window.OnBackInvokedDispatcher;

/** UI-thread-only immersive mode. The WebView keeps its normal insets outside this mode. */
final class NativeFullscreen {
    interface Changed {void changed(boolean enabled,String reason);}
    private final Activity activity;
    private final View content;
    private final Changed changed;
    private final BackRegistration back;
    private boolean enabled;
    private int normalUi,normalCutout,normalBehavior,normalVisibleBars;

    NativeFullscreen(Activity activity,View content,Changed changed,Runnable onBack){
        this.activity=activity;this.content=content;this.changed=changed;
        back=Build.VERSION.SDK_INT>=33?new BackRegistration(activity,onBack):null;
    }

    boolean isEnabled(){return enabled;}

    void setEnabled(boolean requested,String reason){
        if(requested==enabled)return;
        Window window=activity.getWindow();View decor=window.getDecorView();
        if(requested){
            normalUi=decor.getSystemUiVisibility();
            if(Build.VERSION.SDK_INT>=28)normalCutout=window.getAttributes().layoutInDisplayCutoutMode;
            if(Build.VERSION.SDK_INT>=30){
                WindowInsetsController controller=window.getInsetsController();
                if(controller==null)throw new IllegalStateException("Fullscreen is not available until the tablet window is ready.");
                normalBehavior=controller.getSystemBarsBehavior();
                WindowInsets insets=decor.getRootWindowInsets();
                normalVisibleBars=0;
                int[] bars={WindowInsets.Type.statusBars(),WindowInsets.Type.navigationBars(),WindowInsets.Type.captionBar()};
                for(int bar:bars)if(insets!=null&&insets.isVisible(bar))normalVisibleBars|=bar;
                if(insets==null){
                    normalVisibleBars=WindowInsets.Type.navigationBars()|WindowInsets.Type.captionBar();
                    if((window.getAttributes().flags&WindowManager.LayoutParams.FLAG_FULLSCREEN)==0)normalVisibleBars|=WindowInsets.Type.statusBars();
                }
            }
            enabled=true;
            applyImmersive();
        }else{
            enabled=false;
            decor.setSystemUiVisibility(normalUi);
            if(Build.VERSION.SDK_INT>=28){WindowManager.LayoutParams attributes=window.getAttributes();attributes.layoutInDisplayCutoutMode=normalCutout;window.setAttributes(attributes);}
            if(Build.VERSION.SDK_INT>=30){
                // This Activity uses the platform default fitting outside immersive mode.
                // On Android 15+ the target-SDK edge-to-edge policy still takes precedence.
                window.setDecorFitsSystemWindows(true);
                WindowInsetsController controller=window.getInsetsController();
                if(controller!=null){
                    controller.setSystemBarsBehavior(normalBehavior);
                    controller.show(normalVisibleBars);
                    controller.hide(WindowInsets.Type.systemBars()&~normalVisibleBars);
                }
            }
        }
        if(enabled)content.setPadding(0,0,0,0);
        content.requestApplyInsets();
        changed.changed(enabled,reason);
    }

    private void applyImmersive(){
        Window window=activity.getWindow();
        if(Build.VERSION.SDK_INT>=28){
            WindowManager.LayoutParams attributes=window.getAttributes();
            attributes.layoutInDisplayCutoutMode=Build.VERSION.SDK_INT>=30?WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_ALWAYS:WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_SHORT_EDGES;
            window.setAttributes(attributes);
        }
        if(Build.VERSION.SDK_INT>=30){
            window.setDecorFitsSystemWindows(false);
            WindowInsetsController controller=window.getInsetsController();
            if(controller!=null){
                controller.setSystemBarsBehavior(WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE);
                controller.hide(WindowInsets.Type.systemBars());
            }
        }else{
            window.getDecorView().setSystemUiVisibility(normalUi|View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY|View.SYSTEM_UI_FLAG_HIDE_NAVIGATION|View.SYSTEM_UI_FLAG_FULLSCREEN|View.SYSTEM_UI_FLAG_LAYOUT_STABLE|View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION|View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN);
        }
    }

    // Regaining focus can reset system-bar visibility. Edge swipes themselves remain transient.
    void windowFocusChanged(boolean hasFocus){if(hasFocus&&enabled)applyImmersive();}

    void close(){setEnabled(false,"background");if(back!=null)back.close();}

    /** Kept in an API-33-only class so older Android releases do not load these types. */
    private static final class BackRegistration {
        private final OnBackInvokedDispatcher dispatcher;
        private final OnBackInvokedCallback callback;
        BackRegistration(Activity activity,Runnable onBack){
            dispatcher=activity.getOnBackInvokedDispatcher();callback=onBack::run;
            dispatcher.registerOnBackInvokedCallback(OnBackInvokedDispatcher.PRIORITY_DEFAULT,callback);
        }
        void close(){dispatcher.unregisterOnBackInvokedCallback(callback);}
    }
}
