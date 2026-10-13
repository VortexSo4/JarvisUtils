using System;

namespace JarvisUtils.App
{
    public static class Program
    {
        [STAThread]
        public static void Main()
        {
            // Первым делом: установщик/обновлятор Velopack запускает exe с особыми аргументами
            // (установка, удаление, первый запуск после обновления) — их обработка и выход.
            Velopack.VelopackApp.Build().Run();

            var app = new App();
            app.Run();
        }
    }
}