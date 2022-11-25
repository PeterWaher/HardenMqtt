Practical Project
=====================

Objectives to learn:

*	Learn about MQTT, one of the most used protocols in IoT, and the Publish/Subscribe communication pattern
*	That it is not the same to do something in a laboratory or closed network, compared to doing it in an open network, such as the Internet
*	What consequences of vulnerabilities can have effects on the physical world, or our perception of the physical world, which affects our decisions.
*	That cybersecurity is not seen; Lack of cybersecurity can be seen.
*	Cryptography Fundamentals: Asymmetric, Symmetric Encryption and Digital Signatures
*	That transport is not the same as end-to-end transport and because one is used.

Preparation
-----------------

It is necessary to prepare:

1.	A device or program that acts as a sensor: It must detect some property (or simulate detecting a property) in the physical world and have an identity (such as a serial number). It can be a property of the environment, such as temperature, humidity, light, etc., or of a person, or sample, such as pressure, heart rate, weight, sugar, etc.
2.	A screen, with computer or device, where a text is going to be displayed. It must run in an environment/device/computer other than (1).
3.	An MQTT broker, using, for example, an open MQTT broker, such as Mosquitto.   The broker must be accessible on a machine (physical or virtual) on the Internet. It must be accessible through a domain, by both ports 1883 (not encrypted) and 8883 (encrypted with TLS). 

Session 1 - Publish/Subscribe Communication (3 points max)
-------------------------------------------------------------

Objectives to learn:

*	How to connect to an MQTT broker 
*	How to publish information using MQTT
*	How to subscribe to information using MQTT

Tasks:

*	The sensor has to be connected to the MQTT broker. All groups and programs must connect to the same MQTT broker: (1/2 point)
*	The sensor has to publish the measured value, along with the serial number , and the fact that the sensor is used to the MQTT broker. There are several methods one can use to package and represent information. Participants can choose method freely. (1 point)
*	The screen (or the program that controls the screen) has to connect to the MQTT broker. (1/2 point).
*	The display must subscribe to the expected sensor information and present the fact that the sensor is used (or not used), and the value and identity (if it is used). (1 point)

Session 1 tasks should be performed without knowledge of session tasks 2 and 3.

Session 2 - Cyber-insecurity (3 points max)
----------------------------------------------

Objectives to learn:

*	Knowledge of common vulnerabilities in MQTT
	*	Data monitoring/leakage
	*	Altering/injecting values
	*	Depleting resources
*	Know the problem with identities

Tasks:

*	Get values from the sensors of other computers of the other groups (1/(N-1) points, N=number of groups participating, as long as one can identify the sensor value with the sensor identity) 
*	Altering or damaging the values or identities on the screens of other groups. (1/(N-1) points, N=number of groups participating) 
*	Failure the screen program, exhausting program resources (1/(N-1) points, N=number of groups participating) 

Note: Groups may have created secure programs in session 1, causing the other groups to fail to find ways to make the programs fail. Although they do not receive points for that in session 1, the group creating safe programs, manage to lower the scores of the other groups in this session, improving their score relative to the average.

Session 3 - Cybersecurity (6 points max)
------------------------------------------

Objectives to learn:

*	Strengthen their programs using:
	*	Content encryption (which they note that transport encryption does not help)
	*	Digital signatures
	*	Loose coupling (analyzing format, expecting invalid data)
	*	Hiding/Obfuscating Identities

Tasks:

*	Ensures that the screen is secure against denial of service attacks created in session 2 (sending a lot of information, heavy information or invalid information).  (2 points).
*	Ensures that the display only displays values from the associated sensor, using digital signatures. Rule: Keys needed to validate signatures need to be sent by the MQTT broker  itself, so that everyone has access to them. (2 points) 
*	It ensures that no one sees the sensor information, other than the corresponding screen, using symmetric encryption , in combination with key generation using asymmetric algorithms .  Public keys used to generate the secret key need to be sent by the same  MQTT broker, so that everyone has access to them. (2 points)

Note: It is important during this session for groups to try to damage the sensor and screen function of the other groups. By doing so, they lower the scores of the other groups, improving the score of one's group, relative to the average.

