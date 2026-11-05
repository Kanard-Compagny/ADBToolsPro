package fr.kanard.adbtools.diag;

/** Un test du terminal : lancé par ADB Tools, il écrit son résultat avec {@link Out#result}. */
interface DiagTest {
    /** Identifiant du test (camera_back, microphone…), identique côté PC. */
    String id();
    void start();
    /** Arrêt demandé (nouveau test, « Arrêter » sur le PC, appli en pause). Doit libérer le matériel. */
    void stop();
}
