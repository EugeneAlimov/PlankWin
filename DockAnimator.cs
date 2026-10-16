using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace PlankWin
{
    // Собственный покадровый аниматор появления/исчезновения иконок.
    // Работает через модель (AppearScale/AppearOpacity), а не через визуальный контейнер,
    // поэтому не зависит от тайминга генератора контейнеров WPF.
    public class DockAnimator
    {
        private class Anim
        {
            public DockAppConfig App = null!;
            public bool Appearing;      // true = появление, false = исчезновение
            public double Elapsed;      // мс с первого реального кадра
            public DateTime StartTime;  // время первого кадра этой анимации (default до старта)
            public Action? Reposition;  // пересчёт панели (PositionDock)
            public Action? OnComplete;  // по завершении (для исчезновения — удалить из коллекции)
        }

        private readonly List<Anim> _anims = new();
        private bool _hooked;

        private const double Phase1Ms = 350.0; // место + панель
        private const double Phase2Ms = 200.0; // проявление/затухание иконки

        public void AnimateAppear(DockAppConfig app, Action reposition)
        {
            app.AppearScale = 0.0;
            app.AppearOpacity = 0.0;
            Start(new Anim { App = app, Appearing = true, Reposition = reposition });
        }

        public void AnimateDisappear(DockAppConfig app, Action reposition, Action onComplete)
        {
            Start(new Anim { App = app, Appearing = false, Reposition = reposition, OnComplete = onComplete });
        }

        private void Start(Anim a)
        {
            _anims.RemoveAll(x => ReferenceEquals(x.App, a.App));
            _anims.Add(a);
            if (!_hooked)
            {
                _hooked = true;
                CompositionTarget.Rendering += OnRendering;
            }
        }

        private static double EaseOutQuart(double t) => 1.0 - Math.Pow(1.0 - t, 4.0);
        private static double EaseInCubic(double t) => t * t * t;

        private void OnRendering(object? sender, EventArgs e)
        {
            var now = DateTime.Now;

            for (int i = _anims.Count - 1; i >= 0; i--)
            {
                var a = _anims[i];
                if (a.StartTime == default) a.StartTime = now; // первый реальный кадр — точка отсчёта
                a.Elapsed = (now - a.StartTime).TotalMilliseconds;
                bool done = false;

                if (a.Appearing)
                {
                    if (a.Elapsed <= Phase1Ms)
                    {
                        // Фаза 1: место раздвигается + панель растёт синхронно.
                        double t = a.Elapsed / Phase1Ms;
                        a.App.AppearScale = EaseOutQuart(t);
                        a.Reposition?.Invoke();
                    }
                    else if (a.Elapsed <= Phase1Ms + Phase2Ms)
                    {
                        // Фаза 2: иконка проявляется в готовом месте.
                        a.App.AppearScale = 1.0;
                        double t = (a.Elapsed - Phase1Ms) / Phase2Ms;
                        a.App.AppearOpacity = EaseInCubic(t);
                    }
                    else
                    {
                        a.App.AppearScale = 1.0;
                        a.App.AppearOpacity = 1.0;
                        a.Reposition?.Invoke();
                        done = true;
                    }
                }
                else
                {
                    if (a.Elapsed <= Phase2Ms)
                    {
                        // Фаза 1: иконка плавно исчезает (место ещё на месте).
                        double t = a.Elapsed / Phase2Ms;
                        a.App.AppearOpacity = 1.0 - EaseOutQuart(t);
                    }
                    else if (a.Elapsed <= Phase2Ms + Phase1Ms)
                    {
                        // Фаза 2: место стягивается + панель уменьшается (соседи съезжают).
                        a.App.AppearOpacity = 0.0;
                        double t = (a.Elapsed - Phase2Ms) / Phase1Ms;
                        a.App.AppearScale = 1.0 - EaseInCubic(t);
                        a.Reposition?.Invoke();
                    }
                    else
                    {
                        a.App.AppearScale = 0.0;
                        a.App.AppearOpacity = 0.0;
                        done = true;
                    }
                }

                if (done)
                {
                    _anims.RemoveAt(i);
                    a.OnComplete?.Invoke();
                }
            }

            if (_anims.Count == 0)
            {
                CompositionTarget.Rendering -= OnRendering;
                _hooked = false;
            }
        }
    }
}