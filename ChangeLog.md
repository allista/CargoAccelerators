# Cargo Accelerators Change Log

## v0.3.0.1 / 2022-04-01

* Compiled for KSP 1.12.3 and latest AT_Utils
* Internal project changes for CI/CD

## v0.3.0.0

* KSP: 1.11.1
* Orbital Accelerator
    * integrated **textures** made by @JadeOfMaar
    * lowerd mean density to 1.5t/m3
    * decreased deployment and construction speed
    * improved model in-game performance
    * added all colliders
* UI:
    * Added **Construction Widnow** to control segment construcion
    * Construction can now be aborted via UI at any stage
    * Extended the range of PAW controls to 500m
* Added remote connection capability: no more need to enter the loading chamber
    to communicate with an accelerator
* Workforce is updated on vessel change (e.g. on dock) as well as on crew change
* Fixed FPS drop in CONSTRUCTION state

## v0.2.1.1

* Compiled against AT_Utils-1.9.6
* Project maintenance and minor fixes

## v0.2.1

* Separating messages in InfoPane with additional empty line
* Moved InfoPane to its own prefab in AT_Utils.UI
* Adapted to changes in AT_Utils

## v0.2.0.1

* Compiled against KSP-1.10

## v0.2.0.0

* Added **in-orbit barrel construction**
    * Construction is started by pressing a button in PAW.
    * This deactivates the accelerator and creates construction
    scaffold on the end of the barrel that is gradually deployed
    (the scaffold slowly grows in length).
    * Required resources are configured per accelerator.
    * Current Orbital Accelerator requires 
        * Material Kits - 95% of the mass of a segment
        * Specialized Parts - 10% of the mass of a segment
        * Electric Charge - 1000 units per ton
    * Construction also requires qualified Engineers on board
    * To provide both resources and workers the construction
    scaffold has (an invisible) docking port on its end.
* Added **Partial Launch** mode
    * It is activated in the PAW of the accelerator
    * It allows to perform the launch even if the accelerator
    cannot provide the required dV
    * **But** if you have **TCA** installed and the payload has
    its own thrusters, the accelerator will instruct the TCA of
    the payload to continue the maneuver.
* If **TCA** is installed, the recoil compensation is done by the
accelerator automatically.

## v0.1.0.2

* Fixed UI disappearing after scene switch
* Compiled against AT_Utils 1.9.3

## v0.1.0.1

* **Compatible with KSP-1.9**
* Compiled against AT_Utils 1.9.2

## v0.1.0

* Initial release
* Working orbital accelerator mechanics
    * precise dV for the maneuver
    * precise orientation on the maneuver node
    * sophisticated UI
* Working accelerator resizing and barrel extension
    * current model could be extended up to ~1.5km of barrel length
* Rudimentary model
    * no textures
    * almost no colliders
* Orbital accelerator is unbalanced gamewise
    * torque
    * EC production/storage/consumption
    * fuel storage
    * dry mass
* No mechanics for actual gameplay
